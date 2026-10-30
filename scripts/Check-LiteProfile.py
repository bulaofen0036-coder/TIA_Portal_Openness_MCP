# -*- coding: utf-8 -*-
"""Assert that the DEFAULT (lite) profile is a USABLE profile, and that full still differs.

lite is now the default roster for every host, so a model restricted to it must still be able
to walk the golden path end to end, and to reach everything outside it. It could not: ImportFromDocuments / ExportAsDocuments / GetBlocks / GetBlockInfo /
GetCrossReferences were all [L2], so a lite session could open a project and see the tree but
could neither list a block nor use the PREFERRED document import path. Nothing caught that,
because nothing checked it. This does.

It also guards the check itself. When lite became the default, "full" was still being requested
by *unsetting* the env var — so both probes returned the same lite roster and every assertion here
passed vacuously. full must now be requested explicitly AND come back strictly larger.

Usage:  python scripts/Check-LiteProfile.py [path-to-TiaMcpServer.exe]
Exit 0 = lite is self-sufficient, fits the host cap, and can reach everything else.
"""
import json
import os
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "runtime" / "v21" / "TiaMcpServer.exe"

# The documented golden path: orientation -> connect/open -> read -> author -> compile -> save.
# Every name here is referenced by the server instructions, README or GetAuthoringGuide, so a
# profile that omits one is advertising a workflow it cannot perform.
REQUIRED = [
    # orientation / diagnostics
    "Bootstrap", "Doctor", "GetAuthoringGuide", "GetState",
    # session + project
    "Connect", "Disconnect", "OpenProject", "CreateProject", "AttachToOpenProject",
    "CloseProject", "SaveProject", "GetProject", "GetProjectTree", "GetSoftwareTree",
    # read / understand
    "GetBlocks", "GetBlocksWithHierarchy", "GetBlockInfo", "DescribeBlockLogic",
    "GetCrossReferences", "GetPlcTagTables",
    # author (golden path: SD documents preferred, SCL external source alternative)
    "ScaffoldProject", "PlcBuildAndImport", "WritePlcSclSourceFile",
    "ImportFromDocuments", "ExportAsDocuments",
    "ImportBlocksFromDocuments", "ExportBlocksAsDocuments",
    "GenerateBlocksFromExternalSource",
    # verify
    "CompileSoftware", "CompileAndDiagnosePlc",
    # the bridge out of lite — without these two, lite is a dead end for the other ~155 tools
    "FindTools", "CallTool",
]

# VS Code refuses to enable more tools than this; lite exists to stay under it.
HOST_TOOL_CAP = 128


def tools_for_profile(profile):
    """profile=None means 'whatever a user gets with no flag and no env var'."""
    # Always pass --profile explicitly when asking for a specific roster. Relying on
    # "unset the env var" silently stopped meaning "full" the day lite became the default.
    env = dict(os.environ)
    env.pop("TIA_MCP_PROFILE", None)
    args = [str(EXE), "--logging", "0"]
    if profile:
        args += ["--profile", profile]
    p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
                         bufsize=1, env=env)
    seq = [0]

    def send(method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            seq[0] += 1
            msg["id"] = seq[0]
        p.stdin.write(json.dumps(msg) + "\n")
        p.stdin.flush()
        if notify:
            return None
        while True:
            line = p.stdout.readline()
            if not line:
                raise SystemExit("engine closed stdout:\n" + p.stderr.read())
            line = line.strip()
            if not line:
                continue
            try:
                d = json.loads(line)
            except json.JSONDecodeError:
                continue
            if d.get("id") == seq[0]:
                return d

    try:
        send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                            "clientInfo": {"name": "lite-check", "version": "1"}})
        send("notifications/initialized", {}, notify=True)
        return [t["name"] for t in send("tools/list", {})["result"]["tools"]]
    finally:
        try:
            p.stdin.close()
        except Exception:
            pass
        p.terminate()


def main():
    if not EXE.exists():
        print("[FAIL] engine not found:", EXE)
        return 1
    print("engine:", EXE)

    full = tools_for_profile("full")
    lite = tools_for_profile("lite")
    default = tools_for_profile(None)
    print("full profile   : %d tools" % len(full))
    print("lite profile   : %d tools" % len(lite))
    print("default profile: %d tools" % len(default))

    failures = []
    missing = [n for n in REQUIRED if n not in lite]
    if missing:
        failures.append("lite is missing golden-path tools: " + ", ".join(missing))

    unknown = [n for n in REQUIRED if n not in full]
    if unknown:
        failures.append("REQUIRED lists tools this engine does not expose at all: " + ", ".join(unknown))

    if len(lite) > HOST_TOOL_CAP:
        failures.append("lite exposes %d tools, over the %d host cap it exists to respect"
                        % (len(lite), HOST_TOOL_CAP))

    if not lite:
        failures.append("lite exposed no tools")

    # Sentinel: if these ever come back equal, the two probes are no longer probing two
    # different rosters and every assertion above is meaningless.
    if len(full) <= len(lite):
        failures.append("full (%d) is not larger than lite (%d) — the profile probes are not "
                        "distinguishing the two rosters, so this check proves nothing"
                        % (len(full), len(lite)))

    # lite must be what a user gets with no flags and no env var: every generated host
    # config now relies on that default rather than writing the flag out.
    if sorted(default) != sorted(lite):
        failures.append("the default profile (no flag, no env var) is not lite: %d tools vs %d"
                        % (len(default), len(lite)))

    for f in failures:
        print("[FAIL]", f)
    if failures:
        return 1
    print("[ ok ] default is lite; lite covers all %d golden-path tools, stays under the %d cap, "
          "and the other %d tools stay reachable via FindTools/CallTool"
          % (len(REQUIRED), HOST_TOOL_CAP, len(full) - len(lite)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
