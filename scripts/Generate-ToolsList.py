"""Regenerate manifest/tools-list.json from a built MCP engine's real tools/list response."""

import datetime
import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else (
    # The delivered engine, same path every .cmd/.bat launches. Defaulting to the dev build
    # output under tools\...\bin\Release\net48 used to generate a manifest for a different
    # engine than the one customers actually run.
    ROOT / "runtime" / "v21" / "TiaMcpServer.exe"
)
OUT = pathlib.Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else ROOT / "manifest" / "tools-list.json"

# --profile full is REQUIRED, not cosmetic: the engine now defaults to the lite
# roster, so a plain launch would silently write a manifest listing a quarter of the server
# and the bundle validator would then flag the mismatch it just caused. The manifest documents
# what the engine CAN do; which subset a session lists is a separate, per-host decision.
process = subprocess.Popen([str(EXE), "--logging", "0", "--profile", "full"],
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace", bufsize=1)
seq = 0


def release_version():
    """The version at the top of CHANGELOG.md, so the manifest never carries a stale hard-coded one."""
    for line in (ROOT / "CHANGELOG.md").read_text(encoding="utf-8").splitlines():
        m = re.match(r"^## \[(\d+\.\d+\.\d+)\]", line)
        if m:
            return m.group(1)
    return "unknown"


def request(method, params=None):
    global seq
    seq += 1
    message = {"jsonrpc": "2.0", "id": seq, "method": method}
    if params is not None:
        message["params"] = params
    process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
    process.stdin.flush()
    while True:
        line = process.stdout.readline()
        if not line:
            raise RuntimeError("engine exited: " + process.stderr.read())
        response = json.loads(line)
        if response.get("id") == seq:
            if "error" in response:
                raise RuntimeError(str(response["error"]))
            return response["result"]


try:
    request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                           "clientInfo": {"name": "tools-list-generator", "version": "1"}})
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
    process.stdin.flush()
    result = request("tools/list")
    rows = []
    for tool in result.get("tools", []):
        description = tool.get("description", "")
        match = re.match(r"^\[(L\d+)\]\[(?:Category:)?([^\]]+)\]", description)
        schema = tool.get("inputSchema", {}) or {}
        rows.append({
            "name": tool.get("name", ""),
            "layer": match.group(1) if match else "L?",
            "domain": match.group(2) if match else "Misc",
            "method": tool.get("name", ""),
            "returnType": "",
            "parameters": list((schema.get("properties", {}) or {}).keys()),
            "description": description,
        })
    rows.sort(key=lambda item: item["name"].lower())
    document = {
        "package": "TIA_MCP_Delivery_v" + release_version(),
        "generatedAt": datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).isoformat(),
        "source": f"live MCP tools/list of {EXE.name}",
        "toolCount": len(rows),
        "note": "Full roster (--profile full). The default lite profile lists only the core tools; "
                "the rest stay reachable via FindTools + CallTool. Runtime tools/list remains authoritative.",
        "tools": rows,
    }
    OUT.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(rows)} tools to {OUT}")
finally:
    process.terminate()
    try:
        process.wait(5)
    except subprocess.TimeoutExpired:
        process.kill()
