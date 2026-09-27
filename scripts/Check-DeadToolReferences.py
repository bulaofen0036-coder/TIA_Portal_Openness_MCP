"""面向 Agent 的死引用闸：工具描述里点名的工具，必须真的注册过。

为什么要有这道闸：`GetPlcForceTables` 的描述写着 "use SetForceTableEntry"，而
`SetForceTableEntry` 从 0.0.38 起就刻意不再注册（强制写值不许 AI 调）。安全下线做对了，
面向 Agent 的文字忘了同步 —— Agent 照着描述调，撞 "tool not found"，然后自己去找别的
路子绕，而撞上的恰恰是「强制写值」这种安全敏感操作。同类还查到 `GetAxisParameters`、
`GetCpuOnlineState` 两个从来不存在的名字。

这类漂移靠人记不住，只能靠对拍：拿引擎实际注册的名字，扫所有 Agent 能看到的文字。
（同 `SafetyTables.cs` 由 gen 脚本生成的思路：凡是「一份名单必须和代码保持一致」的地方，
都该走生成或加漂移检查。）

用法：
    python scripts/Check-DeadToolReferences.py            # 0=干净 1=有死引用
    python scripts/Check-DeadToolReferences.py --selftest # 哨兵：注入一个假名字，必须被抓到
"""
import re
import io
import os
import sys
import collections

ROOT = 'tools/tiaportal-mcp/src/TiaMcpServer'

# 白名单：形状像工具名、但**不是**本服务器的工具，因此不该被判死引用。
# 每条必须写明它到底是什么 —— 没有理由的白名单等于把闸门关掉。
ALLOWED = {
    # Openness / .NET 的 API 名，描述里是在讲底层调用，不是让 Agent 去调工具
    'GetService': 'Openness IEngineeringObject.GetService<T>()',
    'GetAttribute': 'Openness IEngineeringObject.GetAttribute()',
    'GetAttributeInfos': 'Openness IEngineeringObject.GetAttributeInfos()',
    'GetSupportedFileFormats': 'Openness Workspace.GetSupportedFileFormats()',
    'ConnectObject': 'Openness Workspace.ConnectObject()',
    'ImportDocumentOptions': 'Openness 枚举类型名（importOption 参数的取值来源）',
    'DownloadProvider': 'Openness DownloadProvider 类型名',
    'AddSignalBoard': "TIA 里那个操作的俗称，描述原文是「这就是 'InsertDeviceItem' / 'AddSignalBoard' 操作」",
    # 本仓工具族的通配写法与非工具标识符
    'BuildPlc': 'BuildPlc* 工具族的通配前缀（BuildPlcUdtXml / BuildPlcObXml / …）',
    'CompileError': '错误码取值，不是工具名',
    'RunOut': 'EnsureStartStopUnifiedHmi 建的 HMI 变量名',
    # 明确写着「本服务器没有这个工具」的说明性提及
    'DeleteDb': '描述原文即「没有单独的 DeleteDb/DeleteGlobalDb/DeleteFunctionBlock，用 DeletePlcBlock」',
    'DeleteGlobalDb': '同上',
    'DeleteFunctionBlock': '同上',
    'ImportInstanceTexts': '描述原文即 "not yet exposed"',
    # 下面这些出现在 ServerInstructions / Bootstrap 自检文案 / SKILL.md 里，是内部方法名或 Openness API，不是工具
    'GetHardDeniedReflectionReason': 'Portal 内部的反射拒绝判定方法，自检项的说明文字里点名它',
    'DescribeGuardResult': '同上，自检项内部 helper 名',
    'WriteValue': 'OPC UA 的写值服务名（自检在说「绝不调用它」）',
    'ReadLineAsync': '.NET StreamReader 方法（SKILL.md 讲 HTTP 桥的实现）',
    'CompileUnit': 'SimaticML 元素名',
    'SetPoint': 'SKILL.md 示例里的 PLC 变量名',
    'SetInterval': 'WinCC Unified 运行系统的 JS 函数（HMIRuntime.Timers.SetInterval），画面事件脚本的描述在讲它的用法',
    'ApplyConfiguration': 'Openness ConnectionConfiguration.ApplyConfiguration()',
    'GenerateBlocks': 'Openness PlcExternalSource.GenerateBlocks()',
    'GenerateBlocksFromSource': 'Openness PlcExternalSource.GenerateBlocksFromSource()',
    'BuildExternalSourceImportArguments': 'Portal 内部 helper 名（SKILL.md 讲实现）',
}

# Description(...) 之外，模型还会读到的文字：握手指令 / 编写指南（McpGuides.cs 的全部字符串）、
# Bootstrap 的规则与自检文案（McpServer.cs 的全部字符串）、以及 SKILL.md 里反引号括起的名字。
# 死名字 ImportBlocksFromScl 就是躲在握手指令里、这道闸原来不扫的地方活了好几个版本。
EXTRA_SOURCES = [
    os.path.join(ROOT, 'ModelContextProtocol', 'McpGuides.cs'),
    os.path.join(ROOT, 'ModelContextProtocol', 'McpServer.cs'),
]
SKILL_MD = 'tools/tiaportal-mcp/skill/SKILL.md'
ANY_STR = re.compile(r'@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"', re.S)
TICK = re.compile(r'`([A-Z][A-Za-z0-9]{3,})(?:\(|`)')

VERB = re.compile(
    r'^(Get|Set|Add|Import|Export|Create|Delete|Compile|Download|Sync|Analyze'
    r'|Build|Write|Read|Ensure|Find|List|Describe|Invoke|Generate|Apply|Bind'
    r'|Move|Rename|Save|Open|Close|Connect|Run|Check|Validate|Preflight|Scaffold|Attach)[A-Z]')
STR = r'"[^"]*"'          # 描述文案里没有转义引号，简单形态足够
LIT = re.compile(r'Description\(\s*((?:@?' + STR + r'\s*\+?\s*)+)\)', re.S)
PIECE = re.compile(STR)
TOK = re.compile(r'\b([A-Z][A-Za-z0-9]{3,})\b')


def load(root):
    src = {}
    for dp, _, fs in os.walk(root):
        for f in fs:
            if f.endswith('.cs'):
                p = os.path.join(dp, f)
                src[p] = io.open(p, encoding='utf-8-sig', errors='replace').read()
    return src


def scan(src, extra_text=None):
    """返回 {疑似死引用名: [出处]}。extra_text 供哨兵注入用。"""
    names = set()
    for s in src.values():
        names |= set(re.findall(r'McpServerTool\(Name\s*=\s*"([A-Za-z0-9_]+)"', s))
    items = list(src.items())
    if extra_text:
        items.append(('<sentinel>', extra_text))
    bad = collections.defaultdict(list)
    for p, s in items:
        for m in LIT.finditer(s):
            text = ' '.join(x[1:-1] for x in PIECE.findall(m.group(1)))
            line = s[:m.start()].count('\n') + 1
            for t in set(TOK.findall(text)):
                if t in names or t in ALLOWED or not VERB.match(t):
                    continue
                bad[t].append(os.path.basename(p) + ':' + str(line))

    def flag(t, where):
        if t not in names and t not in ALLOWED and VERB.match(t):
            bad[t].append(where)

    for p in EXTRA_SOURCES:
        s = src.get(p)
        if s is None:
            continue
        for m in ANY_STR.finditer(s):
            line = s[:m.start()].count('\n') + 1
            for t in set(TOK.findall(m.group(0))):
                flag(t, os.path.basename(p) + ':' + str(line))
    if os.path.exists(SKILL_MD):
        md = io.open(SKILL_MD, encoding='utf-8-sig', errors='replace').read()
        for m in TICK.finditer(md):
            flag(m.group(1), 'SKILL.md:' + str(md[:m.start()].count('\n') + 1))
    return names, bad


def main():
    src = load(ROOT)
    if not src:
        print('找不到源码目录 %s —— 请在仓库根目录运行。' % ROOT)
        return 2

    # 哨兵：注入一个必然不存在的工具名，闸门必须抓到它。
    # 这条不是形式主义 —— 本仓吃过「检查自己坏了却全绿」的亏（字段读错致全假 PASS）。
    sentinel = '[McpServerTool(Name = "SentinelTool"), Description("Use GetNonexistentSentinelTool first.")]'
    _, caught = scan(src, extra_text=sentinel)
    if 'GetNonexistentSentinelTool' not in caught:
        print('[FAIL] 哨兵没被抓到 —— 这个检查自己坏了，它的 PASS 不可信。')
        return 2

    names, bad = scan(src)
    print('引擎注册工具：%d 个；扫描文件：%d 个' % (len(names), len(src)))
    if not bad:
        print('[PASS] 工具描述里点名的工具全部真实注册（哨兵已验证闸门有效）。')
        return 0
    print('[FAIL] 下列名字在面向模型的文案里（[Description] / 握手指令 / Bootstrap / SKILL.md）被点名，但没有任何 [McpServerTool] 注册它：')
    for t, locs in sorted(bad.items()):
        print('  %-38s %2d 处  %s' % (t, len(locs), ', '.join(sorted(set(locs))[:4])))
    print('修法：要么改文案说清事实与替代路径，要么把工具真的注册上。'
          '若它本就不是工具名，加进本脚本的 ALLOWED 并写明理由。')
    return 1


if __name__ == '__main__':
    if '--selftest' in sys.argv:
        src = load(ROOT)
        _, caught = scan(src, extra_text='[Description("Use GetNonexistentSentinelTool first.")]')
        ok = 'GetNonexistentSentinelTool' in caught
        print('哨兵自检：' + ('PASS（假名字被抓到）' if ok else 'FAIL（假名字没被抓到）'))
        sys.exit(0 if ok else 1)
    sys.exit(main())
