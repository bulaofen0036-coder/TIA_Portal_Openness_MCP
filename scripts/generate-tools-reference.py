#!/usr/bin/env python3
"""Generate docs/TOOLS_REFERENCE.md + docs/tools-index.csv from the C# sources.

Single source of truth: the [McpServerTool(Name = "..."), Description("...")] attributes
and their method signatures. No TIA / no build required.
"""
import csv
import glob
import os
import re
import sys
from collections import Counter, defaultdict

SRC = os.path.join(os.path.dirname(__file__), "..", "tools", "tiaportal-mcp", "src", "TiaMcpServer")
OUT_MD = os.path.join(os.path.dirname(__file__), "..", "docs", "TOOLS_REFERENCE.md")
OUT_CSV = os.path.join(os.path.dirname(__file__), "..", "docs", "tools-index.csv")

# V19 build excludes this whole file (#if !TIA_V19): TIA V19 has no VCI / MappedObject.
V19_EXCLUDED_FILES = {"McpServer.VersionControl.cs"}


def parse_string_literal(s, i):
    """Parse a C# string literal starting at s[i]. Returns (text, end_index)."""
    if s.startswith('@"', i):
        j = i + 2
        buf = []
        while j < len(s):
            if s[j] == '"':
                if j + 1 < len(s) and s[j + 1] == '"':
                    buf.append('"')
                    j += 2
                    continue
                return "".join(buf), j + 1
            buf.append(s[j])
            j += 1
        raise ValueError("unterminated verbatim string")
    if s[i] == '"':
        j = i + 1
        buf = []
        while j < len(s):
            c = s[j]
            if c == "\\":
                nxt = s[j + 1]
                mapping = {"n": "\n", "r": "\r", "t": "\t", '"': '"', "\\": "\\", "0": "\0"}
                buf.append(mapping.get(nxt, nxt))
                j += 2
                continue
            if c == '"':
                return "".join(buf), j + 1
            buf.append(c)
            j += 1
        raise ValueError("unterminated string")
    raise ValueError("not a string")


def find_matching_paren(s, i):
    """s[i] == '('; return index of matching ')'."""
    depth = 0
    j = i
    while j < len(s):
        c = s[j]
        if c == '"' or s.startswith('@"', j):
            _, j = parse_string_literal(s, j)
            continue
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return j
        j += 1
    raise ValueError("unbalanced parens")


def split_params(block):
    parts, depth, buf, i = [], 0, [], 0
    while i < len(block):
        c = block[i]
        if c == '"' or block.startswith('@"', i):
            text, i2 = parse_string_literal(block, i)
            buf.append(block[i:i2])
            i = i2
            continue
        if c in "([<{":
            depth += 1
        elif c in ")]>}":
            depth -= 1
        if c == "," and depth == 0:
            parts.append("".join(buf).strip())
            buf = []
        else:
            buf.append(c)
        i += 1
    if "".join(buf).strip():
        parts.append("".join(buf).strip())
    return parts


PARAM_RE = re.compile(r"^\s*(?:\[Description\((?P<desc>\"(?:[^\"\\]|\\.)*\"|@\"(?:[^\"]|\"\")*\")\]\s*)?(?P<type>[\w\.<>\[\],\? ]+?)\s+(?P<name>\w+)\s*(?:=\s*(?P<default>.+))?$", re.S)


def parse_param(p):
    p = p.strip()
    desc = ""
    m = re.search(r"\[Description\(", p)
    if m:
        start = m.end(1) - len(m.group(1)) if False else p.index('"', m.end() - 1)
        text, end = parse_string_literal(p, p.index("Description(") + len("Description("))
        desc = text
        p = p[p.index("]", end) + 1:].strip() if "]" in p[end:] else p[end:].strip()
    m = PARAM_RE.match(p)
    if not m:
        return {"type": p, "name": "", "default": "", "desc": desc}
    return {"type": m.group("type").strip(), "name": m.group("name"), "default": (m.group("default") or "").strip(), "desc": desc}


def clean(text):
    return re.sub(r"\s+", " ", text).strip()


def main():
    files = [f for f in glob.glob(os.path.join(SRC, "**", "*.cs"), recursive=True)
             if f"{os.sep}obj" not in f and os.path.basename(f) not in V19_EXCLUDED_FILES]
    tools = []
    for f in files:
        s = open(f, encoding="utf-8-sig", errors="ignore").read()
        for m in re.finditer(r'\[\s*McpServerTool\s*\(\s*Name\s*=\s*"([^"]+)"', s):
            name = m.group(1)
            desc_start = s.find("Description(", m.end())
            if desc_start < 0:
                continue
            str_start = desc_start + len("Description(")
            while s[str_start].isspace():
                str_start += 1
            desc, desc_end = parse_string_literal(s, str_start)
            attr_end = s.find("]", desc_end)
            sig_start = s.find("public static", attr_end)
            if sig_start < 0:
                continue
            paren = s.find("(", sig_start)
            ret_and_name = s[sig_start + len("public static"):paren].strip()
            decl_end = find_matching_paren(s, paren)
            params_block = s[paren + 1:decl_end]
            params = [parse_param(x) for x in split_params(params_block)]
            tools.append({
                "name": name,
                "layer": re.match(r"\[(L\d)\]", desc).group(1) if re.match(r"\[(L\d)\]", desc) else "L2",
                "domain": (re.match(r"\[L\d\]\[([^\]]+)\]", desc) or [None, "Other"])[1].replace("Category:", ""),
                "signature": clean(ret_and_name),
                "description": clean(desc),
                "params": params,
                "file": os.path.relpath(f, SRC),
            })
    # dedupe by name, keep first
    seen, uniq = set(), []
    for t in tools:
        if t["name"] in seen:
            continue
        seen.add(t["name"])
        uniq.append(t)
    tools = sorted(uniq, key=lambda x: (x["domain"], x["name"]))

    domains = defaultdict(list)
    for t in tools:
        domains[t["domain"]].append(t)

    # CSV index
    with open(OUT_CSV, "w", newline="", encoding="utf-8-sig") as fh:
        w = csv.writer(fh)
        w.writerow(["name", "layer", "domain", "parameters", "description"])
        for t in tools:
            w.writerow([t["name"], t["layer"], t["domain"],
                        "; ".join(f'{p["name"]}:{p["type"]}' for p in t["params"] if p["name"]),
                        t["description"]])

    # Markdown
    lines = []
    lines.append("# TIA Portal MCP 工具总览（全部 %d 个）" % len(tools))
    lines.append("")
    lines.append("> 由 `scripts/generate-tools-reference.py` 从源码 `[McpServerTool]` 属性生成，"
                 "与运行时 `tools/list` 一致。生成日期：" + __import__("datetime").date.today().isoformat() + "。")
    lines.append("> 分层：**L0** 只读引导/诊断 · **L1** 高频主力 · **L2** 专用/离线构建器。")
    lines.append(">")
    lines.append("> **V19 注意**：`ExportAsDocuments`、`ExportBlocksAsDocuments`、`ImportFromDocuments`、"
                 "`ImportBlocksFromDocuments` 在 V19 上返回 `NotSupportedOnVersion`（V19 没有 SIMATIC SD 文档 API）；"
                 "5 个 VCI 工具不编译；默认 lite 档，全量工具用 `--profile full` 或 `TIA_MCP_PROFILE=full`。")
    lines.append("")
    lines.append("## 索引")
    lines.append("")
    lines.append("| 域 | 工具数 | 说明 |")
    lines.append("|---|---:|---|")
    domain_desc = {
        "Bootstrap": "会话入口、状态、自检",
        "Portal": "连接、用户组、进程",
        "Project": "工程创建/打开/保存/关闭/树",
        "Hardware": "设备、型号目录、PROFINET",
        "PLC-Software": "块/类型/变量表/编译/外部源",
        "PLC-Builders": "离线 XML / DSL 构建器",
        "Category:PLC-Online": "上线、下载、在线状态",
        "Online-Monitoring": "只读在线监控、S7/OPC UA 读值",
        "HMI": "HMI 通用操作",
        "HMI-Classic": "经典面板（KTP/精智）",
        "HMI-Unified": "WinCC Unified",
        "HMI-Library": "HMI 模板/全局库分析",
        "Category:PLC-Alarms": "报警类别/文本",
        "Category:PLC-OpcUA": "OPC UA 接口",
        "Category:PLC-TechnologyObjects": "工艺对象（TO）",
        "Reports": "验收/诊断/交付报告",
        "Diagnostics": "环境与上下文校验",
        "Reflection": "对象反射查询",
        "Validation": "离线校验套件",
        "PLC-Alarms": "报警类别与报警文本的导入导出",
        "PLC-Online": "上线、下载、在线状态、在线/离线比较",
        "PLC-OpcUA": "OPC UA 接口导入导出与启用开关",
        "PLC-TechnologyObjects": "工艺对象（TO）导入导出",
    }
    for d in sorted(domains):
        anchor = d.lower().replace(":", "").replace(".", "").replace(" ", "-")
        lines.append(f"| [{d}](#{anchor}) | {len(domains[d])} | {domain_desc.get(d, '')} |")
    lines.append(f"| **合计** | **{len(tools)}** | |")
    lines.append("")

    for d in sorted(domains):
        anchor = d.lower().replace(":", "").replace(".", "").replace(" ", "-")
        lines.append(f'<a id="{anchor}"></a>')
        lines.append(f"## {d}（{len(domains[d])} 个）")
        lines.append("")
        for t in domains[d]:
            lines.append(f"### `{t['name']}` <sub>{t['layer']}</sub>")
            lines.append("")
            lines.append(t["description"])
            lines.append("")
            named = [p for p in t["params"] if p["name"]]
            if named:
                lines.append("| 参数 | 类型 | 默认 | 说明 |")
                lines.append("|---|---|---|---|")
                for p in named:
                    dflt = p["default"].replace("|", "\\|")
                    pd = re.sub(rf"^{re.escape(p['name'])}\s*:\s*", "", p["desc"], flags=re.I).replace("|", "\\|")
                    lines.append(f"| `{p['name']}` | `{p['type'][:60]}` | {('`'+dflt+'`') if dflt else ''} | {pd} |")
                lines.append("")
            ret = t["signature"].rsplit(" ", 1)[0] if " " in t["signature"] else t["signature"]
            lines.append(f"返回：`{ret}`")
            lines.append("")
        lines.append("---")
        lines.append("")

    with open(OUT_MD, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))

    print(f"tools: {len(tools)}  domains: {len(domains)}")
    print("params missing name:", sum(1 for t in tools for p in t["params"] if not p["name"]))
    print("md bytes:", os.path.getsize(OUT_MD), "csv bytes:", os.path.getsize(OUT_CSV))
    empty = [t["name"] for t in tools if not t["description"]]
    print("empty desc:", empty)


if __name__ == "__main__":
    main()
