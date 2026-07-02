# TIA Portal MCP Server (v2.2.7 · V20 + V21 · S7DCL · CLI · read-only online monitoring · one-click config)

**English** · [中文](README.md)

> **v2.0 — the same exe is also a declarative CLI (`tia`).** Any AI emits a
> YAML/JSON spec, any engineer runs one command (`tia gen spec.yaml`) — no MCP
> client required. Verbs: `gen` / `patch` / `compile` / `describe` / `export` /
> `import` / `prewarm` / `schema` / `version`. Exit code 0/1/2. See
> `docs/CLI_quickstart.md`. The MCP server behaviour is unchanged.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Release](https://img.shields.io/github/v/release/bulaofen0036-coder/TIA_Portal_Openness_MCP)](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/releases) [![validate-bundle](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/actions/workflows/validate.yml/badge.svg)](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/actions/workflows/validate.yml)

> **Free & open (MIT).** The server runs with **no license key** — there is no
> license-enforcement code at all.

![Architecture](docs/assets/architecture.svg)

Drive **Siemens TIA Portal V20 or V21** from any **MCP** client (stdio or HTTP):
create projects, add hardware, generate PLC objects (Tag / UDT / DB / SCL / LAD),
build **WinCC Unified** screens and events, compile-and-diagnose, and save —
all through natural-language tool calls. The bundle ships **prebuilt runtimes**,
a Skill spec, a static tool list, a capability matrix, PLC/HMI templates,
one-shot project blueprints, and a manual. **No separate source clone required**
to run.

> Works with any MCP-capable client — Cursor, VS Code, Claude Desktop, or your
> own HTTP client — using the same `TiaMcpServer.exe`.

## ⚡ Fastest start (3 steps, no coding — CLI path)

> First time? **No MCP client, no code.** Once TIA is installed, these 3 steps
> generate your first project in minutes.
> (Wiring an AI client like Cursor / Claude Desktop over MCP instead? See
> [Quick Start](#quick-start) below.)

1. **Prepare**: install **TIA Portal V20 or V21** + **.NET Framework 4.8**; add your
   Windows user to the local **`Siemens TIA Openness`** group and log off/on once.
   **Use the exe matching your installed version** — the bundle root ships
   `tia.cmd` (V21) / `tia-v20.cmd` (V20); all other paths are auto-resolved.
2. **Prewarm (optional, recommended)**: double-click `scripts\预热.bat` and leave the
   window open. It keeps one headless TIA resident so every later command connects in
   **~1s** (without it, each run cold-starts ~3 min). Press `Ctrl+C` to close.
3. **Generate a project**: drag a ready-made template
   `templates\project-blueprints\scaffold_spec_motor.json` (or
   `scaffold_spec_start_stop.json`) **onto `scripts\生成工程.bat`** — it creates the
   project → adds PLC/HMI → builds blocks → compiles → saves in one shot. Exit code
   `0` means success.
   - To customize: have any AI emit a spec per [`docs/AI_spec_prompt.md`](docs/AI_spec_prompt.md)
     (YAML or JSON), then drag it onto `生成工程.bat`.
   - CLI equivalent: add the bundle root to PATH, then `tia gen <spec>` (start with
     `--dry-run` for an offline check).

## Highlights

- **Stability-first public generation (v0.0.39).** `PlcBuildAndImport` now returns
  `CapabilityDecision`, `CapabilityWarnings`, and `RecommendedNextActions`;
  `ApplyUnifiedHmiScreenDesignJson(strict=true)` fails when any HMI property write
  fails; `EnsureUnifiedHmiTag(requireVerifiedBinding=true)` requires readback as
  `SymbolicVerified` or `AbsoluteVerified`.
- **Dual-version support (V20 + V21).** Two separate executables, not
  interchangeable: V21 binds the split DLLs (`Siemens.Engineering.Base/Step7/…`),
  V20 binds the monolithic `Siemens.Engineering.dll`.
  - V21 → `tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48/TiaMcpServer.exe`
  - V20 → `tools/tiaportal-mcp/src/TiaMcpServer/bin-v20/Release/net48/TiaMcpServer.exe`
- **Version-safe imports.** Generated Openness XML is normalized to the connected
  portal version on import, so a V20 portal no longer rejects blocks with
  *"engineering version 'V21' is not supported"*.
- **S7DCL textual format.** `ExportAsDocuments` / `ExportBlocksAsDocuments` /
  `ImportFromDocuments` / `ImportBlocksFromDocuments` read/write the diff-friendly
  SIMATIC SD text format (`.s7dcl` + `.s7res`) on V20+ and are flagged *PREFERRED on
  V21+*. The SimaticML XML chain remains for backward compatibility.
- **183 tools** across project, hardware, PLC, HMI, and online operations,
  layered `[L0]`/`[L1]`/`[L2]` so a normal session only needs L0 + L1.

## Requirements

- Windows + **.NET Framework 4.8**
- **TIA Portal V20 or V21** installed
- Current user added to the **`Siemens TIA Openness`** local group (re-login after)

## Quick Start

1. **Locate the portal install root** (one of):
   - pass `--tia-portal-location "D:\app\TIA20\Portal V20"` when launching (recommended for non-default installs);
   - set the `TiaPortalLocation` user environment variable;
   - let it auto-read `HKLM\SOFTWARE\Siemens\Automation\_InstalledSW\TIAP{20|21}\TIA_Opns\Path`.
   With multiple versions installed, pass `--tia-major-version 20` (or `21`) explicitly.
2. **Mount the MCP — one command, fully automatic.**

   ```powershell
   .\tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe config
   ```

   It self-discovers everything: its own absolute path, the installed TIA Portal
   (registry) and version, and the version-matching exe (V20/V21 picked for you) —
   then writes the `tia-portal` entry into every AI host detected on this machine:
   **Claude Desktop / Claude Code / Cursor / VS Code** (existing config backed up
   as `.bak`, other servers preserved). Restart the AI client to load it.
   Options: `config --host vscode` (or `claude|claude-code|cursor`), `config --print`
   to copy a snippet manually. Since v2.2.7 the exe also **self-routes**: if it was
   built for a different TIA major version than the machine has, it transparently
   re-execs the matching sibling exe — grabbing the "wrong" exe no longer crashes.
   Manual fallback: copy the snippet from `cursor-mcp.example.json`, replace
   `REPLACE_ME` with this bundle's root, pick the exe path by TIA version; for
   non-default installs add `"--tia-portal-location","<root>","--tia-major-version","<20|21>"` to `args`.
3. **First call sequence:** `Bootstrap` → `Connect` → `OpenProject` (or
   `CreateProject`) → `GetProjectTree`, then read the real `PLC_*` / `HMI_RT_*`
   paths from the tree before continuing.

### Offline validation (no TIA needed)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate-Bundle.ps1
```

Checks runtime presence, blueprint file completeness, tool-count consistency, and
that PLC/HMI JSON parse. Add `-Strict` for tighter manifest/matrix comparison.

## Build from source

The full source lives under `tools/tiaportal-mcp/src/TiaMcpServer` (51 `.cs`).

```powershell
# V21 (split DLLs)
dotnet build tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.csproj -c Release
# V20 (monolithic DLL) — clean intermediates first if you just built the other target
dotnet build tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V20.csproj -c Release
```

## Capabilities & boundaries

**Can do:** projects & hardware, PROFINET, declarative PLC import, LAD XML import,
WinCC Unified connections / tags (absolute addressing) / screens / button
Down·Up / dynamization, compile-and-diagnose, save.

**Not bundled:** Siemens install media, field projects, business-specific
technology. `reference/` is style/instruction reference only. See `notBundled` in
`manifest/package-manifest.json`. For what Openness **cannot** do, see
`手册/openness-limitations.md`.

## Documentation map

| Path | What |
|------|------|
| `tools/tiaportal-mcp/skill/SKILL.md` | **Primary spec**: tool layers, parameter traps, Unified HMI schema, LAD/SCL boundaries |
| `manifest/tools-list.json` | Static tool names/layers (runtime authority is `tools/list` after connect) |
| `docs/tool-capability-matrix.md` | Capability matrix |
| `docs/full-project-generation-runbook.md` | End-to-end project generation |
| `docs/scl-instruction-library.md` / `docs/lad-instruction-library.md` | SCL / LAD instruction libraries |
| `docs/hmi-connection-driver-matrix.md` | Communication-driver selection by CPU family |
| `手册/quickstart.md` | English quick start |
| `手册/TIA_NL_INTENT_RECIPES.md` | Natural-language → tool-sequence recipes |

## Standard loop (abbreviated)

```text
Bootstrap → Connect → CreateProject → AddDeviceWithFallback → AddHardwareCatalogDeviceWithProbe
→ ConnectDeviceNodesToProfinetSubnet → GetProjectTree → ValidateAutomationContext
→ PlcBuildAndImport(dryRun=true per item) → PlcBuildAndImport(dryRun=false in import order)
→ CompileAndDiagnosePlc → EnsureUnifiedHmiConnection → EnsureUnifiedHmiTagTable → EnsureUnifiedHmiTag
→ EnsureUnifiedHmiScreen → ApplyUnifiedHmiScreenDesignJson → BindUnifiedHmiTagDynamization
→ EnsureUnifiedHmiButtonAction → SaveProject → Disconnect
```
