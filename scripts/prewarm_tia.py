# -*- coding: utf-8 -*-
"""
Keep a headless TIA Portal warm so every MCP session connects in ~1s instead of cold-starting.

Usage:
    python scripts/prewarm_tia.py                 # auto-locate the bundled TiaMcpServer.exe
    python scripts/prewarm_tia.py <path-to-exe>   # or point at a specific exe
    (Ctrl+C to stop; it gracefully disconnects and closes the headless TIA.)

How it works:
    Launches one MCP process and calls Connect, which cold-starts a headless TIA Portal once
    (~10-30s), then holds it open. Later, when your AI client's MCP calls Connect, Openness
    attaches to this already-running instance (~1s). The held instance has no project open, so
    CreateProject/OpenProject from the client land in it. If you also open a TIA GUI with a
    project, the attach prefers that one — no conflict. Stop this script and the headless TIA closes.
"""
import json, subprocess, threading, io, sys, os, time, signal

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

_here = os.path.dirname(os.path.abspath(__file__))
_bundle = os.path.dirname(_here)
# Same order as tia.cmd / 预热.bat: the shipped runtime first (git clone and plugin carry only this),
# the dev build output as the fallback.
EXE = os.path.join(_bundle, "runtime", "v21", "TiaMcpServer.exe")
if not os.path.isfile(EXE):
    EXE = os.path.join(_bundle, "tools", "tiaportal-mcp", "src", "TiaMcpServer", "bin", "Release", "net48", "TiaMcpServer.exe")
if len(sys.argv) > 1:
    EXE = sys.argv[1]
if not os.path.isfile(EXE):
    print(f"找不到 exe: {EXE}\n请把 exe 路径作为参数传入: python scripts/prewarm_tia.py <path-to-TiaMcpServer.exe>", flush=True)
    sys.exit(1)

_id = [0]
def send(proc, method, params=None, notif=False):
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None: msg["params"] = params
    if not notif:
        _id[0] += 1; msg["id"] = _id[0]
    proc.stdin.write((json.dumps(msg) + "\n").encode()); proc.stdin.flush()
    return msg.get("id")

def read_id(proc, want, timeout):
    end = time.time() + timeout
    while time.time() < end:
        box = {}
        def _(): box["l"] = proc.stdout.readline()
        th = threading.Thread(target=_, daemon=True); th.start(); th.join(max(1, end - time.time()))
        line = box.get("l", b"")
        if not line: continue
        try: obj = json.loads(line)
        except Exception: continue
        if obj.get("id") == want: return obj
    return None

def call(proc, tool, args=None, timeout=60):
    i = send(proc, "tools/call", {"name": tool, "arguments": args or {}})
    obj = read_id(proc, i, timeout)
    if obj is None: return None, "<<TIMEOUT>>"
    if "error" in obj: return None, "RPC_ERROR:" + json.dumps(obj["error"], ensure_ascii=False)
    res = obj.get("result", {}); txt = ""
    for c in res.get("content", []):
        if c.get("type") == "text": txt += c.get("text", "")
    return (not res.get("isError", False)), txt

def main():
    print(f"启动 headless MCP: {EXE}", flush=True)
    proc = subprocess.Popen([EXE, "--logging", "0"], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
    i = send(proc, "initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                   "clientInfo": {"name": "prewarm", "version": "1"}})
    init = read_id(proc, i, 30)
    print("  server:", (init or {}).get("result", {}).get("serverInfo"), flush=True)
    send(proc, "notifications/initialized", notif=True)

    print("冷启动 + 保活 headless TIA Portal (首次约 10-30s)...", flush=True)
    t = time.time()
    ok, txt = call(proc, "Connect", {}, timeout=340)
    if not ok:
        print("  ✗ Connect 失败:", txt[:300], flush=True)
        proc.terminate(); sys.exit(1)
    print(f"  ✓ 常驻 headless TIA 就绪 ({time.time()-t:.1f}s)。之后 MCP 会 ~1s attach。保持本窗口开着，Ctrl+C 退出。\n", flush=True)

    stop = {"v": False}
    def _sig(*_): stop["v"] = True
    signal.signal(signal.SIGINT, _sig)
    signal.signal(signal.SIGTERM, _sig)
    try:
        while not stop["v"]:
            for _ in range(60):
                if stop["v"]: break
                time.sleep(1)
            if stop["v"]: break
            ok, _ = call(proc, "GetState", {}, timeout=30)
            ts = time.strftime("%H:%M:%S")
            print(f"  [{ts}] " + ("常驻实例存活。" if ok else "✗ 实例失联，退出。"), flush=True)
            if not ok: break
    finally:
        print("\n正在关闭常驻 headless TIA...", flush=True)
        try: call(proc, "Disconnect", {}, 30)
        except Exception: pass
        proc.terminate()
        print("已退出。", flush=True)

if __name__ == "__main__":
    main()
