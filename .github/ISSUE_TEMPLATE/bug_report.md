---
name: Bug report
about: Report a problem with the MCP server or a tool
title: "[bug] "
labels: bug
---

**Environment**
- TIA Portal version: V16 / V17 / V18 / V19 / V20 / V21 (and Update, e.g. V21 Upd2)
- Branch / bundle: `master` release zip, or `v21` / `v20` / `v19` …
- MCP client: Cursor / VS Code / Claude Desktop / Claude Code / Codex / other
- Server version (Release zip name, e.g. v2.7.8, or as reported by `doctor`):

**`doctor` output**
Run `tia.cmd doctor` (or `tia-v20.cmd doctor`) and paste the result — it settles most environment problems.

**What happened**
A clear description of the bug, including the tool name and the full error text
(the server unwraps the real Openness exception — paste it verbatim).

**To reproduce**
1. Tool called + arguments (redact secrets)
2. ...

**Expected behavior**

**Logs**
Run with `--logging 1` and paste the relevant stderr lines.
