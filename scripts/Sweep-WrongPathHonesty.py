"""给每个只读工具喂一条**不存在的路径**，凡是「返回成功」的都列为嫌疑。

盯的是这一类缺陷：**路径写错却报成功**。
它比崩溃危险得多 —— 调用方（尤其是模型）拿到 isError=false + 一张空清单，
会把「我路径写错了」记成「这个 PLC 里确实没有这种东西」，然后据此继续往下走：
去重建已经存在的块、去汇报一个不存在的结论。错误在这里不会停，只会被放大。

这条检查**离线跑不了**：要判断「路径不存在时的反应」，就得有一个真的项目。
所以它不在 offline-checks 里，是发版前的手工闸门。

用法：
    python scripts/Sweep-WrongPathHonesty.py <项目.ap21> [引擎.exe]

退出码：有嫌疑 = 1，全部正确报错 = 0。

嫌疑不等于缺陷 —— 有些工具「找不到」时返回一个带 ok=false 的结构体也是自洽的。
但每一条都必须**被看过并有意保留**，而不是没人注意到。
"""

import json
import pathlib
import re
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]

if len(sys.argv) < 2:
    print(__doc__)
    sys.exit(2)

PROJECT = str(pathlib.Path(sys.argv[1]).resolve())
EXE = str(pathlib.Path(sys.argv[2]).resolve() if len(sys.argv) > 2
          else ROOT / "runtime" / "v21" / "TiaMcpServer.exe")

# 只调只读工具：前缀白名单 + 关键词黑名单双重过滤。
# 宁可漏掉几个也不能误调写操作 —— 这个脚本是拿真项目跑的。
READ_PREFIX = ("Get", "List", "Describe", "Find", "Probe", "Validate", "Check",
               "Read", "Search", "Analyze", "Preflight", "Inspect", "Diagnose")
DENY = re.compile(r"Set|Write|Delete|Create|Add|Plug|Import|Export|Compile|Download|"
                  r"Upload|Save|Clear|Sync|Connect|Disconnect|Open|Close|Scaffold|"
                  r"Build|Generate|Ensure|Attach|Detach|Go(Online|Offline)|Start|Stop|"
                  r"Apply|Repair|Fix|Rename|Move|Copy|Reset|Run", re.I)

# 故意写错的路径。带 _zzz 后缀是为了不可能和真实对象重名。
BOGUS = {
    "softwarePath": "NoSuchPlc_zzz",
    "plcSoftwarePath": "NoSuchPlc_zzz",
    "deviceItemPath": "NoSuchStation_zzz/NoSuchItem_zzz",
    "devicePath": "NoSuchStation_zzz",
    "blockPath": "NoSuchBlock_zzz",
    "blockName": "NoSuchBlock_zzz",
    "objectPath": "NoSuchBlock_zzz",
    "tagTableName": "NoSuchTable_zzz",
    "objectKind": "Block",
    "maxDepth": 3,
    "changedOnly": True,
}
PATH_ARGS = ("softwarePath", "plcSoftwarePath", "deviceItemPath",
             "devicePath", "blockPath", "objectPath")


class Engine:
    def __init__(self, exe):
        self.p = subprocess.Popen([exe, "--logging", "0", "--profile", "full"],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True,
                                  encoding="utf-8", errors="replace", bufsize=1)
        self.seq = 0
        self.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                    "clientInfo": {"name": "wrong-path-sweep", "version": "1"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.p.stdin.flush()

    def request(self, method, params=None, timeout=600):
        self.seq += 1
        message = {"jsonrpc": "2.0", "id": self.seq, "method": method}
        if params is not None:
            message["params"] = params
        self.p.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.p.stdin.flush()
        deadline = time.time() + timeout
        while True:
            if time.time() > deadline:
                raise RuntimeError("timeout on " + method)
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("engine exited: " + self.p.stderr.read()[:600])
            try:
                parsed = json.loads(line)
            except Exception:
                continue
            if parsed.get("id") == self.seq:
                return parsed

    def call(self, tool, args, timeout=180):
        answer = self.request("tools/call", {"name": tool, "arguments": args}, timeout)
        if "error" in answer:
            return True, json.dumps(answer["error"], ensure_ascii=False)
        result = answer["result"]
        return bool(result.get("isError")), "".join(c.get("text", "") for c in result.get("content", []))

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=60)
        except Exception:
            self.p.kill()


engine = Engine(EXE)
tools = engine.request("tools/list")["result"]["tools"]
engine.call("Connect", {})
failed, opened = engine.call("OpenProject", {"path": PROJECT}, 900)
if failed:
    print("打不开项目：" + opened[:400])
    sys.exit(2)

# 反向哨兵：正确的路径必须照旧成功。少了这条，「所有工具一律报错」的坏实现
# 也能让这个脚本满分通过 —— 那时它测的就不是诚实，而是「有没有全坏」。
sentinel_failed, sentinel_out = engine.call("GetDevices", {})
if sentinel_failed:
    print("[哨兵失败] 正确输入的 GetDevices 都不成功，本次结果不可信：" + sentinel_out[:300])
    engine.close()
    sys.exit(2)

suspects, honest, skipped = [], [], []
for tool in tools:
    name = tool["name"]
    if not name.startswith(READ_PREFIX) or DENY.search(name):
        continue
    required = (tool.get("inputSchema") or {}).get("required") or []
    if not any(r in PATH_ARGS for r in required):
        continue          # 没有路径类必填参的工具，谈不上「路径写错」
    args, missing = {}, []
    for r in required:
        if r in BOGUS:
            args[r] = BOGUS[r]
        else:
            missing.append(r)
    if missing:
        skipped.append((name, missing))
        continue
    try:
        failed, body = engine.call(name, args)
    except RuntimeError as ex:
        suspects.append((name, args, "TIMEOUT: " + str(ex)))
        continue
    if failed:
        honest.append(name)
    else:
        suspects.append((name, args, body.strip()[:300]))

engine.call("CloseProject", {})
engine.close()

print("\n========= 喂错路径的反应 =========")
print("明确报错 %d  |  返回成功（嫌疑）%d  |  缺必填参跳过 %d"
      % (len(honest), len(suspects), len(skipped)))
if suspects:
    print("\n---- 嫌疑：路径不存在却返回成功 ----")
    for name, args, body in suspects:
        print("  %s %s" % (name, json.dumps(args, ensure_ascii=False)))
        print("      -> %s" % body.replace("\n", " ")[:260])
if skipped:
    print("\n---- 未覆盖（还有别的必填参没法自动编）----")
    for name, missing in skipped:
        print("  %s  需要: %s" % (name, ", ".join(missing)))

sys.exit(1 if suspects else 0)
