---
name: tiaportal-mcp
description: Drive Siemens TIA Portal (博途) end-to-end through the TiaMcpServer MCP plugin. Use whenever the user mentions TIA Portal, 博途, STEP 7, WinCC, S7-1200/1500, PLC, HMI, SCL, LAD, STL, Openness, or asks to create/modify/compile/download a project. Always start by calling the `Bootstrap` tool — it returns environment status and the recommended next tool.
---

# TIA Portal MCP — Single Skill

This is the operating skill for TIA Portal MCP automation. The
companion plugin lives at `tools/tiaportal-mcp/`. It exposes on the order of
**~200** MCP tools (lite profile ~42; exact runtime set: call `tools/list` on the running server) covering
project, hardware, PLC, HMI, and online operations.

## 0. Always start here

```
1. Call Bootstrap                        ← returns env, project state, next step
2. Follow RecommendedNextTool            ← e.g. Connect, AttachToOpenProject
3. Call GetProjectTree                   ← resolve real paths (PLC_1, HMI_RT_1)
4. Read-before-write loop                ← inspect, smallest change, compile, save
```

**交付包内最短路径（仅读包内文件时）**  
根目录 `README.md`（三步上手）→ `scripts/Validate-Bundle.ps1`（脱机校验）→ 用 `cursor-mcp.example.json` 把 `command` 指到包内 `TiaMcpServer.exe` → 执行顺序见 `docs/full-project-generation-runbook.md` 与 `templates/project-blueprints/full_plc_hmi_project.json`。

Never guess paths. Never invent SCL/LAD XML. If a tool exists for the task, use
it; otherwise inspect with `DescribeObject`/`DescribeService` first, then call
`InvokeObject`/`InvokeService`.

## 0.1 弱模型 / 新手：你一辈子只需要这 15 个工具（其余的先忽略）

This server exposes ~190 tools. **You do NOT need most of them.** A small or
non-expert model should pick **only** from this whitelist and ignore everything
else unless one of these tools' output explicitly tells you to call another:

| 想做的事 | 用这个，别的别碰 |
|---|---|
| 开局/看环境 | `Bootstrap` → 然后照它返回的 `recommendedNextTool` 做 |
| 连接 TIA | `Connect`（新工程）或 `AttachToOpenProject`（已打开的工程） |
| 看工程里有什么 | `GetProjectTree`（拿真实 `softwarePath`，如 `PLC_1`），细看用 `GetSoftwareTree` / `GetBlocks` |
| **做一个完整项目** | `ScaffoldProject`（一次调用搞定 PLC+HMI，见 §0.5） |
| 加一段 PLC 逻辑/变量/DB/UDT | `PlcBuildAndImport`（先 `dryRun=true`，见 §6.2/§10） |
| 复杂 SCL（带 FOR/CASE/表达式） | `ImportPlcExternalSource` → `GenerateBlocksFromExternalSource`（§14） |
| 编译验证 | `CompileAndDiagnosePlc`（必须 `errorCount=0`） |
| 存盘/收尾 | `SaveProject` → `Disconnect` |
| 环境报错/装不上 | **`Doctor`** — 一次性体检(TIA装没装/Openness组/连接状态)，每项给出修复办法；`fix=true`(默认)会自动把你加进 Openness 组(可能弹 UAC) |

**铁律(弱模型尤其要守):** ① 永远先 `Bootstrap`(或装不上时先 `Doctor`)，照
`recommendedNextTool` 走。② 路径只从 `GetProjectTree` 拿，绝不自己编。③ 写操作前先
`dryRun=true`。④ 收尾必须 `CompileAndDiagnosePlc`(0 错) + `SaveProject`。⑤ 拿不准
参数名时，照本表/§8 的"精确参数名"抄，不要猜。HMI 美化看 §12，库复用看 §15。

**降门槛三件套(已内置，弱模型友好):**
- **Lite 工具档位** — 启动 server 时设环境变量 `TIA_MCP_PROFILE=lite`，`tools/list`
  只暴露 ~42 个 L0/L1 核心工具(而非全部 ~200)，弱模型不会在工具海里选错，VS Code 的
  128 工具上限也不再爆。默认仍是 full；要全量工具就别设这个变量。一键写入宿主配置：
  `tia config --lite`。(v2.2.8 实测：full=200 工具含 L2，lite=42 工具无 L2。)
- **参数容错** — `softwarePath` 现在容忍多余空格/大小写，单 PLC 工程或唯一匹配时
  传"PLC"也能自动认到 `PLC_1`；找不到时报错会**列出可用 PLC 路径**。少数易错工具
  接受别名(`tableJson`↔`tagTableJson`、`screenJson`↔`designJson`、
  `blockJson`↔`fcBlockJson`、`name`↔`projectName`)。即便如此，**优先传准确值**。
- **一键自检自修** — 见上表 `Doctor`。

## 0.5 Fastest path — generate a whole project in ONE call (`ScaffoldProject`)

When the user asks to **create/generate a complete project** (PLC + HMI, "做一个启停/电机/控制项目"), do **not** hand-orchestrate the 20-step runbook. Call **`ScaffoldProject`** once with a single JSON `spec`; it auto-connects, creates the project, adds PLC (+optional Unified HMI) hardware, builds UDTs/DBs/tag tables, imports SCL + LAD blocks, compiles, sets up the HMI connection/screens/tags, and saves — returning a per-step report with compile error counts. This is one model turn instead of twenty, and far less error-prone.

**Ready-made specs** (copy, then replace `__BUNDLE__` with the bundle root absolute path; all blocks/HMI are verified to compile with 0 errors):

```
templates/project-blueprints/scaffold_spec_start_stop.json   启停控制 (PLC+HMI)
templates/project-blueprints/scaffold_spec_motor.json        电机控制 (启停闭锁 + 速度标定 + HMI)
```

Minimal spec (everything else has defaults — only `projectName` is required):

```json
{ "projectName": "MyProj",
  "plcName": "PLC_1", "plcFamily": "S7-1500",
  "hmiName": "HMI_1", "hmiSoftwarePath": "HMI_RT_1",
  "udt": [ { /* same json as PlcBuildAndImport kind=udt */ } ],
  "globalDb": [ { /* kind=globaldb json */ } ],
  "tagTable": [ { /* kind=tagtable json */ } ],
  "sclSourceFiles": [ "C:\\bundle\\templates\\plc\\scl-examples\\FB_BasicLatch.scl" ],
  "ladDocs": [ { "importPath": "C:\\bundle\\...\\skill\\lad-cookbook", "name": "MCPVerify_FC_LAD" } ],
  "hmiScreens": [ { "screenName": "主画面", "width": 800, "height": 480, "designJson": { /* §6.3 schema */ } } ],
  "hmiTags": [ { "tagTableName": "Default tag table", "tagName": "Tag_Run", "hmiDataType": "Bool", "address": "%DB100.DBX0.0" } ],
  "compile": true, "save": true }
```

Notes:
- Omit `hmiName` to skip all HMI. Omit any of `udt/globalDb/tagTable/sclSourceFiles/ladDocs/hmiScreens/hmiTags` to skip that part.
- `udt/globalDb/tagTable` items are the **exact** json shapes from §6.2 (what `PlcBuildAndImport` accepts).
- `designJson` is the §6.3 Unified HMI schema. Size the screen to the panel's native resolution (WinCC Unified PC default 800×480) or it will be clipped.
- HMI tags: use an absolute PLC address (`%DB100.DBX0.0`, `%MD10`) so the binding read-back verifies.
- Critical-step failures (connect/createProject/PLC device) abort and throw; per-element failures are collected in the returned `steps` so you can see exactly what to fix and re-run.
- For per-session fast connects, keep a warm headless instance running (`_prewarm_tia.py`); see §0.

Only fall back to the manual runbook (`docs/full-project-generation-runbook.md`) when the user needs something `ScaffoldProject` does not cover.

## 1. Tool layers

The `Description` of every tool starts with one of three layer tags:

| Tag | Meaning | When to use |
|---|---|---|
| `[L0]` | Bootstrap / read-only diagnostics | First call of a session, environment checks |
| `[L1]` | Common workflow tool | 80% of normal sessions only need L0+L1 |
| `[L2]` | Domain / advanced tool | Reach for these by name only after L0/L1 fails or when a specific need arises |

Core L0/L1 set:

```
L0  Bootstrap, GetState, RunCapabilitySelfTest, RunOnlineMonitoringSafetySelfTest,
    GenerateAcceptanceReport, GenerateErrorReport
L1  Connect, Disconnect, AttachToOpenProject, OpenProject, CreateProject,
    ScaffoldProject, SaveProject, CloseProject, GetProjectTree, GetSoftwareTree, GetSoftwareInfo,
    PlcBuildAndImport, CompileSoftware, CompileAndDiagnosePlc,
    DownloadToPlc, CheckDownloadReadiness, GoOnline, GoOffline, GetOnlineState,
    EnsureOpennessUserGroup, ListPortalProcessProjects, GetProject,
    GetDevices, AddDeviceWithFallback, SearchHardwareCatalog,
    ImportBlock, ImportType, ImportPlcTagTable,
    ConnectDeviceNodesToProfinetSubnet, ValidateAutomationContext
```

## 2. Connecting an AI client to the MCP server

### stdio (Claude Desktop, Cursor, VS Code MCP)

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "C:\\path\\to\\TiaMcpServer.exe",
      "args": []
    }
  }
}
```

### HTTP (any client that speaks JSON-RPC)

```powershell
TiaMcpServer.exe --transport http --http-prefix http://127.0.0.1:8765/ --http-api-key <secret>
```

Endpoints:

| Method + Path | Purpose |
|---|---|
| `POST /mcp` | One JSON-RPC message per request |
| `GET /mcp/health` | Liveness + session count + build version |
| `DELETE /mcp` | Terminate session (best-effort) |

Auth (when `--http-api-key` is set): either header works — pick whichever your
client supports:

```
Authorization: Bearer <secret>
X-API-Key: <secret>
```

Set `Accept: text/event-stream` to get the response wrapped as a single SSE
message event (for spec-compliant clients). Otherwise the response is plain JSON.

`Mcp-Session-Id` is generated on the first call and echoed back; subsequent
calls may include it for client-side correlation. State is **not** isolated
across sessions because TIA Portal itself is process-wide.

### 调用方式怎么选（避免踩坑）

| 场景 | 推荐 |
|------|------|
| **Cursor / Claude Desktop / VS Code（MCP stdio）** | `mcpServers.command` 指向包内 `TiaMcpServer.exe`，`args: []`。由客户端完成 MCP 握手，直接调 `tools/call`。 |
| **健康检查 / 是否已启动** | `GET /mcp/health`（仅探活，不替代 MCP 协议） |
| **HTTP 自写脚本** | 需实现 **完整** MCP JSON-RPC 会话（如 `initialize`、部分场景下 SSE / `Mcp-Session-Id`），**不要**对 `POST /mcp` 只发单条裸 `tools/call` 就期望返回，否则易长时间阻塞。 |

## 3. Read-before-write workflow (the only one that matters)

```
GetProjectTree                       resolve PLC/HMI paths
ExportBlock|GetBlockInfo|...         inspect what already exists
PlcBuildAndImport (kind=fc|udt|...)  smallest safe change
CompileSoftware                      must end with errors=0 warnings=0
SaveProject                          persist to disk
```

`PlcBuildAndImport` is the preferred entry for declarative PLC objects (UDT,
tag table, GlobalDB, FC, FB) — it generates Openness XML and imports in one
call. Use the lower-level `ImportBlock`/`ImportType`/`ImportPlcTagTable` only
when you already have hand-crafted XML.

## 4. What this MCP cannot do (V21 PublicAPI limits)

These have NO Openness API — do not try to invent reflection workarounds:

- Read or change CPU operating mode (RUN/STOP/STARTUP) → use OPC UA
- Read CPU fault/diagnostic buffer → use OPC UA
- ClearForces / Unforce / per-block selective download
- Trigger Safety F-CPU compile (must be done in TIA UI manually)

Force/Watch table tools edit the project-side definition; values become
effective only after the project is online and the table trigger fires.

Full list (this bundle): `手册/openness-limitations.md` (bundle root: same folder as `README.md`).

## 5. Encoding & PowerShell traps (script drivers only)

- The server itself sets `Console.InputEncoding = Console.OutputEncoding = UTF-8`
  on startup (since v0.0.27 + the 2026-05-11 patch). Without that, Chinese
  project names / device names / `commentZhCn` payloads become `???` over stdio.
  If you fork an older build, you must add it yourself.
- Generated `.scl` external sources and any Chinese text destined for TIA
  import must be **UTF-8 with BOM**.
- PowerShell 5.1 reads `.ps1` as the system code page (GBK on zh-CN Windows)
  unless the file is UTF-8-with-BOM. Any path containing Chinese (e.g.
  Chinese characters) breaks `Process.Start` if the script itself is bare UTF-8.
- `.NET Framework 4.8` does NOT have `ProcessStartInfo.StandardInputEncoding`
  (Core-only API). To send UTF-8 to a child server's stdin from PowerShell:

  ```powershell
  $proc = [System.Diagnostics.Process]::Start($psi)
  $stdinUtf8 = New-Object System.IO.StreamWriter(
      $proc.StandardInput.BaseStream,
      (New-Object System.Text.UTF8Encoding($false)))
  $stdinUtf8.AutoFlush = $true
  $stdinUtf8.WriteLine($jsonRpcLine)   # use this; do NOT use $proc.StandardInput
  ```

- Reading stdio responses: cache the pending `ReadLineAsync` task between
  iterations. Calling `ReadLineAsync` twice on the same stream before the
  first one completes throws `"流正在由其上的前一操作使用"`.

  ```powershell
  if ($null -eq $script:pending) { $script:pending = $proc.StandardOutput.ReadLineAsync() }
  if (-not $script:pending.Wait($remainMs)) { continue }
  $line = $script:pending.Result; $script:pending = $null
  ```

- Tool failures often surface as `result.isError = true` with
  `content[0].text = "An error occurred invoking 'X'"` rather than as a
  JSON-RPC `error`. Check both shapes; the real exception text is on stderr.
- Don't read tool-call response text via regex — the server JSON-encodes
  Chinese as `\uXXXX`. Always `ConvertFrom-Json` the `text` field first, then
  read named properties (`.items[0].name`, `.tree`, ...).

## 6. Bundle-only docs + copy-paste JSON for `PlcBuildAndImport` / Unified HMI

Paths below are relative to the **delivery bundle root** (the folder that contains `README.md`, `手册/`, and `tools/`).

### 6.1 Authoritative files shipped in this bundle

| Need | Path |
|---|---|
| Setup + MCP wiring | `手册/quickstart.md` |
| What Openness cannot do | `手册/openness-limitations.md` |
| Error model | `手册/error-model.md` |
| NL → tool sequences (16 scenarios) | `手册/TIA_NL_INTENT_RECIPES.md` |
| Static tool roster | `manifest/tools-list.json` |
| Tool capability matrix | `docs/tool-capability-matrix.md` |
| Full PLC+HMI project blueprint | `templates/project-blueprints/full_plc_hmi_project.json` |
| Full project runbook | `docs/full-project-generation-runbook.md` |
| Offline bundle validation (no TIA start) | `scripts/Validate-Bundle.ps1` |
| IDE-neutral MCP + tool list authority | `docs/mcp-ide-and-tool-visibility.md` |
| HMI↔PLC symbolic / absolute / red-tag troubleshooting | `docs/hmi-plc-tag-binding-and-addressing.md` |
| Optional `reference/` sample projects (outside bundle) | `docs/optional-reference-materials.md` |
| PLC network & instruction expansion patterns | `docs/plc-network-patterns-expanded.md` |
| Importable LAD XML samples | `tools/tiaportal-mcp/skill/lad-cookbook/*.xml` |
| External SCL sources (UTF-8 BOM on disk) | `tools/tiaportal-mcp/skill/scl-cookbook/*.scl` |

The static files are for planning and parser grounding; run `tools/list` on the
live MCP server for the authoritative runtime roster. There is **no** bundled
Siemens STEP7/WinCC manual tree.

### 6.2 `PlcBuildAndImport` — minimal `json` shapes (always `dryRun=true` first)

Pass `json` as a **string** (escape quotes in MCP args). Replace
`softwarePath` / group paths with values from `GetProjectTree`.

**`kind=udt`**

```json
{"name":"UDT_MCP_Demo","members":[{"name":"Speed","datatype":"Int","externalWritable":false}]}
```

**`kind=tagtable`**

```json
{"tableName":"MCP_DemoTags","tags":[{"name":"DemoRun","dataTypeName":"Bool","logicalAddress":"%M0.0"}]}
```

**`kind=globaldb`**

```json
{"dbName":"GDB_MCP_Demo","dbNumber":1,"staticMembers":[{"name":"Counter","datatype":"Int","startValue":"0"}]}
```

**`kind=fc`** (ST body from `structuredText.operations`; `op` includes `if`,
`elsif`, `else`, `endif`, `assignment`, `line`, …)

```json
{
  "blockName":"FC_MCP_Demo",
  "blockNumber":1,
  "inputs":[{"name":"InRun","datatype":"Bool"}],
  "outputs":[{"name":"OutOk","datatype":"Bool"}],
  "structuredText":{"operations":[{"op":"assignment","target":"OutOk","literalValue":"TRUE"}]}
}
```

**`kind=fb`**

```json
{
  "blockName":"FB_MCP_Demo",
  "blockNumber":2,
  "inputs":[{"name":"En","datatype":"Bool"}],
  "outputs":[{"name":"Busy","datatype":"Bool"}],
  "statics":[],
  "structuredText":{"operations":[{"op":"assignment","target":"Busy","literalValue":"FALSE"}]}
}
```

### 6.3 Unified HMI — minimal `designJson` for `ApplyUnifiedHmiScreenDesignJson`

Keys are **lowercase**. Colors: ARGB hex strings like `0xAARRGGBB`. Call
`EnsureUnifiedHmiScreen` before apply. Button bit actions:
`EnsureUnifiedHmiButtonAction` with `eventType` **`Down` / `Up` / `Tapped`**
( **`Pressed` / `Released` are wrong** ). Full recipe + schema: **§12** below.

```json
{
  "screen":{"BackColor":"0xFFF8FAFC"},
  "items":[
    {"type":"Rectangle","name":"Panel","left":24,"top":80,"width":400,"height":200,"properties":{"BackColor":"0xFFFFFFFF","BorderWidth":1}},
    {"type":"Text","name":"Lbl","left":40,"top":100,"width":200,"height":28,"text":"Demo","font":{"Size":16}},
    {"type":"Button","name":"StartBtn","left":40,"top":160,"width":120,"height":44,"text":"Start"},
    {"type":"IOField","name":"SpFld","left":180,"top":160,"width":100,"height":40}
  ]
}
```

## 7. Common end-to-end shapes

### Build a minimal new project

```
Bootstrap → Connect → CreateProject(<dir>, <name>_<timestamp>)
→ AddDeviceWithFallback(CPU)
→ AddHardwareCatalogDeviceWithProbe(HMI)       (optional)
→ ConnectDeviceNodesToProfinetSubnet           (optional)
→ PlcBuildAndImport(kind=tagtable, ...)
→ PlcBuildAndImport(kind=globaldb, ...)
→ PlcBuildAndImport(kind=fc|fb, ...)           (one per logic unit)
→ CompileSoftware                              (must be 0/0)
→ SaveProject → Disconnect
```

### Build a *complete* example project (not a 2-tag toy)

When the user asks for a "demo / 示例 / example project", the minimal recipe above
produces something that looks empty. A good example **must** include, at minimum:

- **PLC:** ≥1 UDT (e.g. `UDT_Motor`), a global DB instanced from it, a tag table
  with named I/O (not just `%I0.0`), and ≥2 logic blocks (e.g. an FB with the
  control logic + an FC or OB1 that calls it). Reuse the verified blocks in
  `demo-assets/plc/` (`UDT_Motor`, `FB_StartStop`, `FC_StartStop`, `Main`).
- **HMI:** a styled Main screen built with the §12 aesthetic recipe (title bar +
  status cards + buttons + IOField + indicator lamps) — **not** a bare button or
  two. Do not stop at `EnsureStartStopUnifiedHmi`; that is a wiring shortcut, not a
  finished screen. Always follow it with `ApplyUnifiedHmiScreenDesignJson` (§12).
- **Binding:** every HMI tag bound to a real PLC tag through the connection (below).
- Compile to `0/0` and `SaveProject` before declaring done.

#### Standard HMI variable connection & driver (do this, every time)

The non-standard binding users complain about comes from skipping these:

1. **One connection, correct driver.** `EnsureUnifiedHmiConnection` (Unified) /
   `HMI_Connection_1` (Classic) auto-selects the PLC driver from the CPU
   `TypeIdentifier` (S7-1200/1500 vs 300/400). Never hand-name a driver; never
   point two tag tables at different ad-hoc connection names.
2. **Symbolic binding, not absolute.** Bind each HMI tag to a **named PLC tag**
   (`ControllerTag` = `Conveyor.Start`, `AddressAccessMode=Symbolic`) — never to a
   raw `%DB1.DBX0.0`. Absolute addresses break on PLC recompile/reorder.
3. **Acquisition cycle 100 ms** for interactive controls; slower (1 s) for
   read-only displays — don't leave everything on the default.
4. The PLC tag/DB member **must exist before** the HMI tag binds to it, or the
   binding silently drops. Build PLC side first, then HMI.

### Deploy to a real CPU

```
Bootstrap → AttachToOpenProject
→ CompileSoftware
→ CheckDownloadReadiness
→ DownloadToPlc(keepActualValues=true)
→ GetOnlineState → SaveProject
```

### Watch/Force values

```
Bootstrap → AttachToOpenProject → GoOnline
→ SetForceTableEntry(address, value)           force, persistent until cleared
→ SetWatchTableModifyValue(address, value)     one-shot modify
→ GoOffline
```

For more scenarios (alarms, OPC UA, multi-language text, Unified HMI pages)
see `手册/TIA_NL_INTENT_RECIPES.md` (bundle root).

## 8. Frequently used tools — exact parameter names

The table below lists **exact parameter names** that commonly trip parsers. When
in doubt, confirm with `tools/list` / `Bootstrap` on your build.

| Layer | Tool | Required args (verified names) | Notes |
|---|---|---|---|
| L0 | `Bootstrap` | — | Read-only orientation; returns `{ready, environment, portal, recommendedNextTool, toolLayers, knownLimits}` |
| L0 | `GetState` | — | Cheap probe; returns `{isConnected, project, session}` |
| L0 | `RunCapabilitySelfTest` | `inspectPortalProcesses=false`, `includeProjectTree=false` | ~15 ms when light; sets pass/fail per capability |
| L1 | `Connect` | — | Attaches to a running TIA process; first call may pop Openness auth dialog in TIA UI |
| L1 | `AttachToOpenProject` | `projectName` (must match the leaf shown in TIA window title) | Cleanest path when a project is already open. Avoids `CreateProject` pollution |
| L1 | `GetProject` | — | Lists open projects + multi-user sessions |
| L1 | `GetProjectTree` | — | Returns ASCII tree string — parse with `Devices`/`PLC Software` markers |
| L1 | `GetDevices` | — | Returns `items[].name` (e.g. `PLC_1`) and `description` |
| L1 | `SearchHardwareCatalog` | `keyword` (e.g. `"1211C"`) | ~500 ms; needs Connect; returns `count` + `items[]` |
| L1 | `GetSoftwareInfo` | `softwarePath` (e.g. `"PLC_1"`) | Returns class name (e.g. `Siemens.Engineering.SW.PlcSoftware`) |
| L1 | `GetSoftwareTree` | `softwarePath` | Returns ASCII tree with `Program blocks`, `PLC tags`, etc. |
| L1 | `GetBlocks` | `softwarePath` | Returns `items[]` with `typeName`/`name`/`programmingLanguage` (LAD/SCL/STL) |
| L1 | `PlcBuildAndImport` | `softwarePath`, `kind=tagtable`, `json={tableName,tags:[{name,dataTypeName,logicalAddress}]}`, `dryRun=true` | dryRun writes XML to `%TEMP%\tia_mcp_plc_build_import_*` without touching project |
| L1 | `PlcBuildAndImport` | `softwarePath`, `kind=globaldb`, `json={dbName,dbNumber,staticMembers:[{name,datatype,startValue}]}`, `dryRun=true` | Same pattern |
| L1 | `PlcBuildAndImport` | `softwarePath`, `kind=fc`, `json={blockName,blockNumber,inputs,outputs,structuredText:{operations:[...]}}`, `dryRun=true` | `op` ∈ `if`/`elsif`/`endif`/`assignment`/`line` |
| L2 | `BuildPlcTagTableXml` | `tagTableJson` (note: NOT `tableJson`) | Pure offline; returns `{xml}` |
| L2 | `ComposePlcFcBlockXml` | `fcBlockJson` | Pure offline; returns `{xml}` |
| L2 | `BuildClassicHmiScreenXml` | `designJson={Screen:{Name,Width,Height},Items:[{Type,Name,Left,Top,Width,Height,Text}]}` (PascalCase) | Pure offline; for Classic/Basic HMI |
| L2 | `GetOnlineState` | `softwarePath` | Returns `{state:"Offline"\|"Online", isOnline, isReachable, message}` |
| L2 | `CheckDownloadReadiness` | `softwarePath` | Returns `{ready, hasDownloadProvider, hasConfiguration, isConsistent}` |
| L1 | `SaveProject` | — | Verified safe on attached project |
| L1 | `Disconnect` | — | Always end with this |

### Attach-mode workflow (no pollution, preferred when TIA already has a project open)

```
Bootstrap                                     ← env + recommendedNextTool
Connect                                       ← may need Openness UI click on first call
GetProject  →  items[0].name                  ← internal Project.Name; TIA window title may differ
AttachToOpenProject(projectName=<that name>)  ← reuse existing project
GetProjectTree                                ← never guess paths
                                               regex 'PlcSoftware:\s*([^\s\[]+)' → all PLC softwarePaths
GetDevices                                    ← returns station containers, not CPUs
                                               → use GetProjectTree to find real PLC names
GetSoftwareTree / GetBlocks / GetSoftwareInfo ← inspect
PlcBuildAndImport(dryRun=true)                ← validate XML without modifying project
GetOnlineState / CheckDownloadReadiness       ← read-only diagnostics
Disconnect
```

### Real-write on a Chinese-named device (verified 2026-05-11 against `安全PLC`)

```
Connect
GetProject                                     → "江夏测试项目V21-260511"
AttachToOpenProject(projectName="江夏测试项目V21-260511")
GetProjectTree                                → discover "PlcSoftware: 安全PLC"
PlcBuildAndImport(softwarePath="安全PLC", kind="tagtable", json=…, dryRun=false) → 10s, ok
PlcBuildAndImport(softwarePath="安全PLC", kind="globaldb", json=…, dryRun=false) → 5s, ok
PlcBuildAndImport(softwarePath="安全PLC", kind="fc",       json=…, dryRun=false) → 5s, ok
GetBlocks(softwarePath="安全PLC", namePattern="MCPVerify_*") → confirms imported blocks
CompileSoftware(softwarePath="安全PLC")        → 18s, errorCount=0 (warnings ok)
CheckDownloadReadiness / GetOnlineState        → ready=true / state=Offline
SaveProject → Disconnect
```

Use a unique prefix (`MCPVerify_`, `MCP_`, etc.) for any object you write into a
real shared project — the user can find and delete them in TIA UI later.

### Common parameter-name traps

| Tool | Wrong (silently 500s) | Correct |
|---|---|---|
| `BuildPlcTagTableXml` | `tableJson` | `tagTableJson` |
| `BuildClassicHmiScreenXml` | `screenJson` | `designJson` |
| `ComposePlcFcBlockXml` | `blockJson` | `fcBlockJson` |
| `AttachToOpenProject` | `name` | `projectName` |

PowerShell-side: call shape that works with `tools/call`:

```powershell
$resp = Send-Request 'tools/call' @{ name='PlcBuildAndImport'; arguments=@{
    softwarePath='PLC_1'; kind='tagtable'; dryRun=$true
    json='{"tableName":"DefaultTagTable","tags":[{"name":"Start","dataTypeName":"Bool","logicalAddress":"%I0.0"}]}'
} } 30000
```

## 9. Generating LAD — prefer S7DCL text over FlgNet XML

**Decision rule (read this first):**

| You want… | Use | Why |
|---|---|---|
| Any contact / coil / SR / compare / Move / math ladder | **S7DCL text** (`.s7dcl` + `.s7res`), import via `ImportBlocksFromDocuments` (documents path) | Concise, LLM-writable, round-trips, no UId/wire bookkeeping. The only practical way to author general ladder. |
| A network that is purely *call one FC with parameters* | `ComposePlcLadFcBlockXml` / `BuildFlgNetCallXml` tool | The single supported XML builder — it **only** does FC-call networks |
| General ladder as hand-written FlgNet XML | **avoid** | Brittle (decimal-vs-hex `UId`, manual wire graph, entity escaping). This is the usual cause of "梯形图报错". |

There is **no MCP tool that builds contact/coil/compare FlgNet XML** (`LadNetworkBuilder` is not wired up). So for normal ladder, **write `.s7dcl`** — do not hand-roll FlgNet XML.

### 9a. LAD via S7DCL (PREFERRED, verified V21 round-trip)

Author two paired files, **both UTF-8 *with* BOM**:
- `Name.s7dcl` — block declaration + LAD networks
- `Name.s7res` — `MLC_*` text IDs → localized strings (**this is where Chinese comments/titles live**)

Verified references — copy these, change names + logic:
```
skill/lad-cookbook/MCPVerify_FC_LAD.s7dcl    + .s7res  (FC: 串联/并联/SR/比较/Move/Add)
skill/lad-cookbook/MCPVerify_FB_LAD_v3.s7dcl + .s7res  (FB: 定时器放 Static)
skill/lad-cookbook/MCPVerify_Mixed_LADSCL.s7dcl + .s7res  (FB: LAD+SCL 混编 — 互锁/比较+定时/SCL算术/边沿,
                                                          全部 import-verified 形式; 2026-06-11 编译0错)
```

Grammar (distilled from the verified sample):
```
{ S7_BlockComment := "MLC_548"; S7_BlockNumber := "901";
  S7_BlockTitle := "MLC_4Vm"; S7_Optimized := "TRUE";
  S7_PreferredLanguage := "LAD"; S7_Version := "0.1" }
FUNCTION "MCPVerify_FC_LAD" : Void
    VAR_INPUT  "A" : Bool; SET : Bool; VAL : Int; END_VAR
    VAR_OUTPUT OUT_AND : Bool; OUT_SR : Bool; DST : Int; END_VAR

    { S7_Language := "LAD"; S7_NetworkComment := "MLC_4X9"; S7_NetworkTitle := "MLC_3fA" }
    NETWORK
        RUNG wire#powerrail                       -- series AND
            Contact( #"A" ) Contact( #"B" ) Coil( #OUT_AND )
        END_RUNG
    END_NETWORK

    NETWORK                                        -- parallel OR via wire#w1
        RUNG wire#powerrail Contact( #"A" ) wire#w1 Coil( #OUT_OR ) END_RUNG
        RUNG wire#powerrail Contact( #"B" )        END_RUNG wire#w1
    END_NETWORK

    NETWORK RUNG wire#powerrail Contact( #SET )   S_Coil( #OUT_SR ) END_RUNG END_NETWORK
    NETWORK RUNG wire#powerrail Contact( #RESET ) R_Coil( #OUT_SR ) END_RUNG END_NETWORK

    NETWORK                                        -- compare: VAL > 100
        RUNG wire#powerrail
            { S7_Templates := "SrcType := Int" }
            GT_Contact( in1 := #VAL, in2 := 100 ) Coil( #OUT_GT )
        END_RUNG
    END_NETWORK

    NETWORK RUNG wire#powerrail Move( in := 42, out1 => #DST ) END_RUNG END_NETWORK
    NETWORK
        RUNG wire#powerrail
            { S7_Templates := "SrcType := Int" }
            Add( in1 := #V1, in2 := #V2, out => #SUM )
        END_RUNG
    END_NETWORK
END_FUNCTION
```
Element vocabulary: `Contact`/`Coil`/`S_Coil`/`R_Coil`, parallel branches joined by a
`wire#wN` label, `GT_Contact`/`LT_Contact`/… + `{ S7_Templates := "SrcType := Int" }`,
`Move( in:=, out1=> )`, `Add`/`Sub`/`Mul`/`Div( in1:=, in2:=, out=> )`. `.s7res` `id:`
values must match every `MLC_*` referenced in `.s7dcl`. For instructions not shown here
(常闭/negated contact, edges, timers, `Calc`…), **export a real block that uses them with
`ExportBlocksAsDocuments` and copy the exact `.s7dcl` syntax** — BUT beware: **the export form of
several instructions does NOT re-import** (export grammar ≠ import grammar). Verified 2026-06-11:
`Gt`/`Lt`/`Ne`/`Eq{ SrcType }( IN1, IN2 )`, `PBox(...)`, and `Move{ Card:=1; DisableENO:=TRUE }( IN, OUT1 )`
(all from real-block *exports*) **fail `ImportFromDocuments`** with a generic "Failed importing".
The **import-verified** forms are below.

**Real-block verified vocabulary — IMPORT forms** (江夏5T车, hand-authored blocks, compile 0 errors,
2026-06-11). ⚠️ author the import form (left), never the export form (right):

| Need | S7DCL **import** form (author this) | Export form (do NOT author) |
|---|---|---|
| 常开 / 常闭 contact | `Contact( #x )` / `I_Contact( #x )` | — |
| Negate power flow | `Not()` | — |
| Coil / Set / Reset | `Coil( … )` / `S_Coil( … )` / `R_Coil( … )` | — |
| Compare | `GT_Contact`/`LT_Contact`/`EQ_Contact`/`NE_Contact``{ SrcType := DInt }( in1 := …, in2 := … )` | `Gt`/`Lt`/`Eq`/`Ne{ … }( IN1, IN2 )` ✗ |
| Move | `Move( in := …, out1 => … )` | `Move{ Card:=1; DisableENO:=TRUE }( IN, OUT1 )` ✗ |
| Edge (rising) | `P_Trig( #memBit )` (in series; `#memBit` = edge-memory bit) | `PBox( … )` ✗ |
| IEC timer box (global DB) | `"Global_Data".Global_TON[11].TON{ time_type := Time }( PT := T#2s, ET => )`; `TOF`/`TP` same | (same) |
| IEC timer box (FB static) | `#tonInst.TON{ time_type := Time }( PT := T#500ms, ET => )` | (same) |
| Symbolic global operand | `Contact( "DB".path.member )` / `Coil( "DB".path.member )` | — |
| Call FC / FB | `"FCName"()` / `"InstanceDB"( p := …, o => … )` | — |

`Neg` is not import-verified — keep negate networks in SCL.

**Branch (wire) model** — `wire#wN` is a junction: tap *out* (`RUNG wire#wN …` = parallel branch from
that point) + join *in* (`… END_RUNG wire#wN` = OR back into junction wN).

**Titles must be `MLC_*` refs (in `.s7res`) or omitted** — inline literal titles
(`S7_NetworkTitle := "中文"`) **fail on import**; strip or convert to `MLC_*`.

**Mixed LAD+SCL (verified V21):** a block with `S7_PreferredLanguage := "LAD"` can interleave
`{ S7_Language := "SCL" } NETWORK <scl statements> END_NETWORK` with LAD networks. A pure-SCL block
cannot hold LAD networks — author in LAD when you need the mix. See cookbook
`MCPVerify_Mixed_LADSCL.s7dcl`.

**Per-network LAD/SCL judgment** — author **LAD** for interlock / limit / brake-timer / workstep-trigger
/ indicator logic (electricians read power-flow); keep **SCL** for arithmetic/scaling, multi-param
FC/FB calls, state machines, comms, complex condition aggregation. **Do not force LAD.** (Validated on
江夏5T车: 02/03/04/09 + A3_2/3/5 reauthored mixed, 0 errors; A0_1/A4_1/comms kept SCL.)

Import (verified MCP workflow, 2026-06-11):
```
# 1) write Name.s7dcl + Name.s7res, BOTH UTF-8 *with* BOM, in one dir
ImportFromDocuments(softwarePath="<plc>", groupPath="<group path or empty>",
                    importPath="<dir>", fileNameWithoutExtension="<BlockName>",
                    importOption="Override")   # Override replaces same-name block & keeps its number
#   batch: ImportBlocksFromDocuments(softwarePath=, groupPath=, importPath=, regexName="")
CompileAndDiagnosePlc(softwarePath="<plc>")     ← errorCount must be 0
```
- `.s7res` must be present even when titles omitted (minimal: one `MLC_x` with `zh-CN:` + `en-US:`).
- `ImportBlockFromScl` / `ImportBlocksFromScl` are thin aliases of the two import tools above.

> **Boundary (known TIA limitation):** importing **LAD** from SD documents can fail
> unless every `.s7res` item also has an **`en-US`** tag, not only `zh-CN`. The
> bundled samples round-tripped on a V21 zh-CN machine with `zh-CN` only, but if
> `ImportBlocksFromDocuments` fails on a LAD block, **add an `en-US:` line beside each
> `zh-CN:` in the `.s7res`** and retry. (See README "Known Limitations".)

### 9b. LAD via FlgNet XML (fallback — FC-call tool, or last-resort hand edit)

LAD blocks live in `<FlgNet xmlns="http://.../FlgNet/v5">` with two collections:
`Parts` (operands + instructions) and `Wires` (pin-to-pin energy flow).

A 7-network reference FC that imports cleanly (`errorCount=0`) and covers the
core instruction set is at:

```
tools/tiaportal-mcp/skill/lad-cookbook/MCPVerify_FC_LAD.xml
```

It exercises:

| Network | Instruction(s) | Part Name(s) | Pin set |
|---|---|---|---|
| 1 | Two contacts in series → coil | `Contact`, `Contact`, `Coil` | `in/out/operand` |
| 2 | Two contacts in parallel (OR) → coil | `Contact`, `Contact`, `O`, `Coil` | OR-box: `in1/in2/out` |
| 3 | Set coil | `Contact`, `SCoil` | `in/operand` |
| 4 | Reset coil | `Contact`, `RCoil` | `in/operand` |
| 5 | Compare `>` literal → coil | `Gt` (`<TemplateValue Name="SrcType" Type="Type">Int</TemplateValue>`), `Coil` | Compare: `pre/in1/in2/out` |
| 6 | Move literal → variable | `Move` (`<TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>`) | Move: `en/eno/in/out1` |
| 7 | Add Int+Int → Int | `Add` (SrcType+Card templates) | Add/Sub/Mul/Div: `en/eno/in1/in2/out` |

Verified `Part Name` registry (more exist; these are the ones live-tested):

```
Contact          常开触点 (add <Negated Name="operand"/> for 常闭)
Coil / SCoil / RCoil   线圈 / 置位 / 复位
O                并联 OR-box (TemplateValue Name="Card" = inputs count)
PBox / NBox      上升沿 / 下降沿
Gt / Lt / Eq / Ne / Ge / Le   比较 (TemplateValue SrcType=Int|DInt|Real|Word|...)
Add / Sub / Mul / Div         算术 (SrcType + Card templates)
Move             传送 (Card=1 normally)
TON / TOF / TP   IEC 定时器 (require <Instance Scope="LocalVariable|GlobalVariable" UId="…"><Component Name="..."/></Instance>; only inside FB or with explicit IDB)
Calc             表达式块 (<Equation>...</Equation> + Card + SrcType)
Serialize / Deserialize / SCATTER / GATHER   字节级转换
```

Connection reference (`Wires` rules):

```
<Wire UId="…">
  <Powerrail/>                左端母线（能流入口）
  <NameCon UId="P" Name="…"/> 接到 Part P 的命名引脚
  <NameCon UId="P2" Name="…"/> 多个 NameCon = 并联同时驱动多个 Part
</Wire>

<Wire><IdentCon UId="V"/><NameCon UId="P" Name="operand"/></Wire>
                                     变量/字面量 V 接到 P 的 operand/in/...
<Wire><NameCon UId="P1" Name="out"/><NameCon UId="P2" Name="in"/></Wire>
                                     P1.out 串联到 P2.in
```

### LAD pitfalls (these all bit me — read once, save hours)

1. **`UId` inside `<FlgNet>` MUST be decimal `xs:int`**, NOT hex. Block-level
   `ID` attributes ARE hex strings (`"A"`, `"B"`, `"10"`, `"1A"`...) and they
   live in a separate namespace. Mixing them gives the cryptic Simatic ML
   error: `UId 属性无效 - 类型 …XMLSchema:int 的值 "2A" 无效`.
2. **Strip every `<!-- -->` XML comment** before import — Openness rejects them.
3. **Escape `&` `<` `>`** in any `<Text>`/comment — TIA reports
   `分析 EntityName 时出错。 第 N`.
4. The `ProgrammingLanguage` element appears **twice**: once at block level
   (`<SW.Blocks.FC>/AttributeList/ProgrammingLanguage>LAD`) and once per
   `CompileUnit` (`AttributeList/ProgrammingLanguage>LAD`). Mixing SCL and LAD
   networks is allowed if you set the per-CompileUnit value accordingly.
5. Importing `Contact + Coil + Compare/Move/Add` to a **safety PLC** standard
   block group works — these are standard instructions; safety F-FCs need
   different builders we don't ship yet.
6. After `ImportBlock`, server now surfaces the real Openness exception
   chain (Portal.cs `UnwrapImportError`, since 2026-05-11). Don't reinterpret
   `"Import failed"` — read everything after the colon.

To create a new LAD FC, copy `MCPVerify_FC_LAD.xml`, change `Name`, `Number`,
`Interface/Sections`, and rebuild networks. Then:

```
ImportBlock(softwarePath="<plc>", groupPath="", importPath="<your.xml>")
CompileSoftware(softwarePath="<plc>")        ← errorCount must be 0
```

## 10. SCL via DSL (verified 2026-05-11)

`PlcBuildAndImport(kind=fc, json={…structuredText.operations})` is the supported
DSL. Verified ops: `assignment`, `if`, `else`, `endif`, `line`, `token`,
`blank`, `newline`, `symbol`, `local`, `global`, `literal`.

```jsonc
{
  "blockName": "MyFc", "blockNumber": 902,
  "inputs":  [{"name":"Reset","datatype":"Bool"}, {"name":"Speed","datatype":"Real"}],
  "outputs": [{"name":"Out","datatype":"Real"}, {"name":"Mode","datatype":"Int"}],
  "structuredText": { "operations": [
    {"op":"assignment","target":"Out","literalValue":"0.0"},
    {"op":"if","condition":"Reset"},
      {"op":"assignment","target":"Out","literalValue":"0.0","indent":1},
      {"op":"assignment","target":"Mode","literalValue":"0","indent":1},
    {"op":"else"},
      {"op":"line","indent":1,"items":[
        {"sym":"Out"},{"token":":="},{"sym":"Speed"},{"token":"*"},{"lit":"1.5"}
      ]},
      {"op":"assignment","target":"Mode","literalValue":"1","indent":1},
    {"op":"endif"}
  ]}
}
```

### SCL DSL limits (known)

- `if/elsif` `condition` and `assignment` `source` accept a **single variable
  name** only — NOT expressions like `Mode = 1`, `Setpoint - Actual`,
  `Disable OR FaultLatch`, `ABS(x)`, or the literals `TRUE`/`FALSE`.
  **The builder now hard-errors at build/`dryRun`** on such input (e.g.
  `SCL 局部符号非法："RawMax <> RawMin"`) instead of silently emitting a
  variable named after the whole expression — which used to slip through
  `dryRun` and only blow up at TIA compile as `Tag #"…" not defined`.
- For multi-variable conditions, fall back to `op:"line"` (free-form token
  list, but it always appends `;` and newline, so it can't emit standalone
  `IF cond THEN` headers).
- `for`, `while`, `case`, `return`, `exit`, `continue`, `repeat` are NOT
  supported by the DSL. **Preferred path: write a native `.scl` and import via
  `ImportPlcExternalSource` + `GenerateBlocksFromExternalSource` (§14)** — see
  `templates/plc/scl-examples/*.scl` for ready FC/FB examples. (Hand-writing the
  `<StructuredText>` token AST also works but is far more error-prone.)
- `String`/`WString` outputs may compile-error in some safety standard groups;
  test with `dryRun=true` first.

## 11. LAD v2 — extended instructions (verified 2026-05-11 against `安全PLC`, errorCount=0)

A second cookbook FC adds 10 more instructions on top of §9. Imports cleanly
and compiles with errorCount=0 on Safety PLC standard side:

```
tools/tiaportal-mcp/skill/lad-cookbook/MCPVerify_FC_LAD_v2.xml   ← FC 902
```

| Network | Instruction | Part Name + required template values | Wire pin set |
|---|---|---|---|
| 1 | Eq Int | `Eq` `<TemplateValue Name="SrcType" Type="Type">Int</TemplateValue>` | `pre/in1/in2/out` |
| 2 | Ne Int | `Ne` SrcType=Int | same |
| 3 | Le Int | `Le` SrcType=Int | same |
| 4 | Ge Int | `Ge` SrcType=Int | same |
| 5 | Sub Int | `Sub` `DisabledENO="true"` SrcType=Int | `en/eno/in1/in2/out` |
| 6 | Mul Int | `Mul` SrcType=Int + `Card=2` | same |
| 7 | Div Int | `Div` SrcType=Int | same |
| 8 | Mod Int | `Mod` SrcType=Int | same |
| 9 | Convert Int→Real | `Convert` `<TemplateValue Name="SrcType">Int</TemplateValue>` `<TemplateValue Name="DestType">Real</TemplateValue>` | `en/eno/in/out` |
| 10 | Negated contact | `Contact` + child `<Negated Name="operand"/>` | `in/out/operand` |

Combined with §9, the verified native-LAD instruction set is:
contacts (NO/NC) · S/R coils · OR-box · Compare (Eq/Ne/Lt/Gt/Le/Ge) ·
Math (Add/Sub/Mul/Div/Mod) · Convert · Move.

Import `skill/lad-cookbook/MCPVerify_FC_LAD.xml` via `ImportBlock` and compile;
the FC encodes the v2 instruction sweep (8 networks).

### LAD v3 — timers **must not** live in FC `Temp` on F-CPU; use FB `Static` or DB

**Rule (F-CPU / 安全 PLC):** `TON` / `TOF` / `TP` **IEC timer instances** must
**not** be declared in an **FC** `Temp` section (not allowed → compile errors).
Valid options: **(1)** `TON_TIME` in **`FB` → `Static`** (with `SetPoint` on the
static member when the export shows it — see `Speed_Ctrl.xml`), **(2)** timer
in a **global DB** and `Instance Scope="GlobalVariable"` in LAD (see
`07-操作选择.xml`), **(3)** author in TIA and `ExportBlock`.

**Repo layout:**

| File | Role |
|---|---|
| `skill/lad-cookbook/MCPVerify_FC_LAD_v3.xml` | FC **59990**, **Lt** only — quick LAD import sanity check |
| `skill/lad-cookbook/MCPVerify_FB_LAD_v3.xml` | FB **59989**, **Static** `tonInst : TON_TIME` + networks **TON**, **`PBox`**, **`Not`**, **`Lt`** |

Stage both XML files to a temp folder, import **FC then FB** with
`ImportBlock` into the PLC software path from `GetProjectTree`, then
`CompileSoftware` until `errorCount=0`.

**`PBox` wiring:** same operand for contact and `bit` needs **two** `Access`
entries with **different** `UId`s (two `IdentCon`s) — see `07-操作选择.xml` in a
full repo export if you have one; otherwise follow the `MCPVerify_FB_LAD_v3`
export in `lad-cookbook`.

## 12. Unified HMI workflow (verified 2026-05-11 against `HMI_RT_1`, 20/20 PASS)

The project's HMI is **WinCC Unified**, NOT Classic. The `BuildClassicHmi*` /
`ImportHmi*` tools are Classic-only and will silently fail on Unified targets.
Use the `EnsureUnifiedHmi*` family.

### End-to-end aesthetic screen recipe

```
EnsureUnifiedHmiConnection(hmiSoftwarePath="HMI_RT_1",
                           connectionName="HMI_Conn_X",
                           plcName="<PLC name from GetProjectTree>")
EnsureUnifiedHmiTagTable(hmiSoftwarePath="HMI_RT_1", tagTableName="MyTags")
EnsureUnifiedHmiTag(hmiSoftwarePath="HMI_RT_1", tagTableName="MyTags",
                    tagName="StartCmd", hmiDataType="Bool",
                    plcName="<PLC software node>",
                    plcTag="DB_HMI_Interface.CmdEnable",
                    connectionName="HMI_Conn_X",
                    address="%DB200.DBX0.0")
                    ← omit PLC binding if PLC tag does not yet exist
EnsureUnifiedHmiScreen(hmiSoftwarePath="HMI_RT_1",
                       screenName="Main", width=1024, height=768)
ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath="HMI_RT_1",
                                screenName="Main",
                                designJson="<see schema below>")
EnsureUnifiedHmiButtonAction(hmiSoftwarePath="HMI_RT_1", screenName="Main",
                             buttonName="StartBtn",
                             eventType="Down", actionKind="set-bit",
                             targetTag="StartCmd")
SaveProject
```

### `ApplyUnifiedHmiScreenDesignJson` schema (verified)

All keys are **lowercase**. Colors are TIA ARGB hex `0xAARRGGBB` strings.

```jsonc
{
  "screen": { "BackColor": "0xFFF8FAFC" },
  "items": [
    {
      "type": "Rectangle" | "Text" | "Button" | "IOField" | "<full CLR type>",
      "name": "TitleBar",                    // unique on this screen
      "left": 0, "top": 0, "width": 1024, "height": 72,
      "text": "可选文本，自动包成 zh-CN MultilingualText",
      "textProperty": "Text",                // optional, default "Text"
      "properties": {                         // forwarded to reflection setter
        "BackColor": "0xFF0F172A",
        "ForeColor": "0xFFF8FAFC",
        "BorderColor": "0xFFCBD5E1",
        "BorderWidth": 1
      },
      "font":    { "Size": 22 },             // → ScreenItem.Font part
      "content": { "..." : "..." },          // → ScreenItem.Content part
      "padding": { "..." : "..." }           // → ScreenItem.Padding part
    }
  ]
}
```

Returns `meta.changed[]` (created/updated items) and `meta.failed[]` (per-property
write failures, e.g. unknown property name).

#### Complete dashboard `designJson` (copy this, don't ship a bare button)

A 1024×768 starting point that looks finished — dark title bar, two status cards,
Start/Stop buttons, a speed `IOField`, and a run-state lamp. Uses **only** the
verified keys above; adjust text/positions, then bind buttons (`EnsureUnifiedHmiButtonAction`)
and the IOField/lamp tags. This is the "rich + 美化" target; do not stop short of it.

```jsonc
{
  "screen": { "BackColor": "0xFFF1F5F9" },
  "items": [
    { "type": "Rectangle", "name": "TitleBar", "left": 0, "top": 0, "width": 1024, "height": 72,
      "properties": { "BackColor": "0xFF0F172A" } },
    { "type": "Text", "name": "TitleText", "left": 24, "top": 20, "width": 600, "height": 36,
      "text": "电机控制 · Motor Control", "font": { "Size": 22 },
      "properties": { "ForeColor": "0xFFF8FAFC", "BackColor": "0x000F172A" } },

    { "type": "Rectangle", "name": "CardRun", "left": 32, "top": 110, "width": 300, "height": 150,
      "properties": { "BackColor": "0xFFFFFFFF", "BorderColor": "0xFFCBD5E1", "BorderWidth": 1 } },
    { "type": "Text", "name": "CardRunLabel", "left": 52, "top": 126, "width": 260, "height": 28,
      "text": "运行状态", "font": { "Size": 16 }, "properties": { "ForeColor": "0xFF334155", "BackColor": "0x00FFFFFF" } },
    { "type": "Rectangle", "name": "RunLamp", "left": 52, "top": 170, "width": 40, "height": 40,
      "properties": { "BackColor": "0xFF22C55E", "BorderColor": "0xFF15803D", "BorderWidth": 2 } },
    { "type": "Text", "name": "RunLampText", "left": 104, "top": 176, "width": 200, "height": 28,
      "text": "RUN", "font": { "Size": 18 }, "properties": { "ForeColor": "0xFF166534", "BackColor": "0x00FFFFFF" } },

    { "type": "Rectangle", "name": "CardSpeed", "left": 364, "top": 110, "width": 300, "height": 150,
      "properties": { "BackColor": "0xFFFFFFFF", "BorderColor": "0xFFCBD5E1", "BorderWidth": 1 } },
    { "type": "Text", "name": "CardSpeedLabel", "left": 384, "top": 126, "width": 260, "height": 28,
      "text": "转速 (rpm)", "font": { "Size": 16 }, "properties": { "ForeColor": "0xFF334155", "BackColor": "0x00FFFFFF" } },
    { "type": "IOField", "name": "SpeedIO", "left": 384, "top": 168, "width": 180, "height": 44,
      "properties": { "BackColor": "0xFFF8FAFC", "BorderColor": "0xFFCBD5E1", "BorderWidth": 1 }, "font": { "Size": 20 } },

    { "type": "Button", "name": "StartBtn", "left": 720, "top": 120, "width": 260, "height": 64,
      "text": "启动 START", "font": { "Size": 20 },
      "properties": { "BackColor": "0xFF16A34A", "ForeColor": "0xFFFFFFFF", "BorderColor": "0xFF15803D", "BorderWidth": 1 } },
    { "type": "Button", "name": "StopBtn", "left": 720, "top": 200, "width": 260, "height": 64,
      "text": "停止 STOP", "font": { "Size": 20 },
      "properties": { "BackColor": "0xFFDC2626", "ForeColor": "0xFFFFFFFF", "BorderColor": "0xFFB91C1C", "BorderWidth": 1 } }
  ]
}
```

(For dynamic lamp color / value display, bind via `BindUnifiedHmiTagDynamization`;
card backgrounds use opaque `0xFF…`, text-over-card uses transparent `0x00…` so the
card shows through.)

### `HmiButtonEventType` (probed from V21 Openness — only these are accepted)

```
None, Activated, Deactivated, Tapped, KeyDown, KeyUp, Down, Up, ContextTapped
```

`Down` = press, `Up` = release. **`Pressed` / `Released` / `Press` / `Release` /
`Click` are NOT valid** in V21 and produce `System.ArgumentException: 未找到请求的值`
deep inside `SetUnifiedHmiButtonEventScriptCode`.

### `EnsureUnifiedHmiButtonAction` `actionKind` values

`set-bit`, `reset-bit`, `toggle-bit` (other recipes are rejected by the safety
gate). The tool builds and applies the script via
`SetUnifiedHmiButtonEventScriptCode` — i.e. it actually writes JS to the event
handler, then runs SyntaxCheck.

### Unified HMI pitfalls

1. `EnsureUnifiedHmiConnection` — `plcName` must be the **PLC software** node name
   from `GetProjectTree` (e.g. `"PLC_1"` or `"PLC_Main"`). The tool resolves the actual
   PLC device, station, PROFINET node and CPU family from the project before writing
   `Partner` / `Station` / `Node` and `CommunicationDriver`. Re-run it after hardware
   insertion or subnet changes, then check readback for the real PLC partner instead of
   blank Partner/Station/Node.
2. `EnsureUnifiedHmiTag` — for the delivery blueprint, pass both `plcTag` and `address`.
   `plcTag` is the symbolic DB member such as `DB_HMI_Interface.CmdEnable`; `address` is
   the verified runtime address such as `%DB200.DBX0.0`. The HMI interface DB is standard
   access (`MemoryLayout=Standard`, `DB200`), so absolute HMI addresses connect to real
   PLC memory even when Unified symbolic readback is empty on a local TIA build.
3. `EnsureStartStopUnifiedHmi` — 会先调用 `EnsureUnifiedHmiConnection`，再用与
   `EnsureUnifiedHmiTag` **相同** 的规则写 **符号互连**（清掉错误的 `%DB1…` 绝对地址），
   可选参数：`plcName`、`connectionName`（默认 `HMI_Connection_1`）。HMI 标签表名
   默认 `默认变量表`，PLC 符号需与 `StartPB`/`StopPB`/`EStop`/`RunOut` 一致。
4. **Full visuals vs. “chat minimal JSON”** — `ApplyUnifiedHmiScreenDesignJson` only draws
   what you pass in `designJson`. The **curated multi-page layouts** live under
   `templates/hmi/unified_*.json` (shadows, cards, IO fields, footers). For production-like
   screens, **read a template file → minify → pass as `designJson`**, then bind
   dynamizations and `EnsureUnifiedHmiButtonAction` / `SetUnifiedHmiButtonEventScriptCode`.
   A few rectangles in chat are **not** “the template is ugly”; they skip the template.
5. Apply layout BEFORE wiring button actions. The button must exist as a
   ScreenItem before `EnsureUnifiedHmiButtonAction` can resolve it; otherwise
   you get `Screen item 'StartBtn' not found on screen '...'`.
6. `BackColor` etc. on `properties` use ARGB hex like `0xFFRRGGBB`. RGB triples
   (`"30, 41, 59"`) silently land in `meta.failed[]`.
7. Probe the available API surface with
   `ListUnifiedHmiApiTypes(nameContains="<filter>")` when you hit an enum or
   property name you're not sure about — example:
   `nameContains="ButtonEvent"` returned `HmiButtonEventType` enum and family.

End-to-end recipe (Connect → tags → screen → design → button actions → Save)
matches **§12** in this file; exercise it on your own Unified RT project.

## 13. Real download — V21 cast bug (FIXED 2026-06-17, verified on a real CPU)

`DownloadToPlc(softwarePath=…)` used to fail on V21 with:

```
类型 "Siemens.Engineering.Connection.ConnectionConfiguration" 的对象
无法转换为类型 "Siemens.Engineering.Connection.IConfiguration"
```

Root cause + fix in `Portal.cs::DownloadToPlc` (each step verified against the V21 PublicAPI):
1. **Cast**: `ConnectionConfiguration` does NOT implement `IConfiguration`, but a
   `ConfigurationTargetInterface` (`Modes → PcInterfaces → TargetInterfaces`) DOES.
   `TrySelectDownloadTargetInterface` navigates to the first target and passes it to
   `Download()`. (⚠ auto-selects the FIRST PG/PC interface — on a multi-NIC PC confirm the adapter.)
2. **Stop prompt**: `StopModulesSelections` is `{ NoAction, StopAll }`; the handler used a
   non-existent `"StopModule"` that silently parsed to nothing, leaving the prompt "unhandled"
   and aborting every download. Fixed to `StopAll`.
3. Reflection-invoke failures are now unwrapped (`TargetInvocationException`) so the caller sees
   the real reason.

**Verified 2026-06-17 on a real S7-1200 (江夏 安全PLC): `DownloadToPlc` →
`state=Success, 0 errors` (stop → download existing program → restart).**

`CheckDownloadReadiness` is project-side only (`ready=true` ⇒ blocks consistent + a network
configuration exists). Note `GetOnlineState` reports the TIA **online-monitoring** session, not
raw reachability, so it can read `Offline` even when a download succeeds.

## 14. SCL external source files (`DeletePlcExternalSource` / `ImportPlcExternalSource` / `GenerateBlocksFromExternalSource`)

**Root cause (fixed in plugin, 2026-05-11):** Siemens documents
`PlcExternalSourceComposition.CreateFromFile(string name, string path)` — the
**first argument is the external-source name** (usually `MyBlock.scl`), the
second is the **full path** on disk. The MCP server previously built
`(string, string)` argument lists as `(FullPath, titleWithoutExtension)`, which
invokes the wrong overload order and surfaces as a misleading *"method … Create
… not supported by the current version"* `EngineeringTargetInvocationException`.

**Fix in `Portal.cs`:** `BuildExternalSourceImportArguments` now emits
`(fi.Name, fi.FullName)` and `(fileTitleWithoutExtension, fi.FullName)` for
two-string signatures; `ImportPlcExternalSource` tries the `ExternalSources`
composition **before** the parent group; `GenerateBlocksFromExternalSource`
tries `GenerateBlocks()` then `GenerateBlocksFromSource(PlcBlockUserGroup,
GenerateBlockOption)` via reflection when a zero-parameter generator is absent.

**Verification:** with TIA running and a project open, import the UTF-8 BOM
`.scl` files from `skill/scl-cookbook/` via `ImportPlcExternalSource`, run
`GenerateBlocksFromExternalSource`, then `CompileSoftware` until
`errorCount=0`. If Connect/`GetProject` fails, fix the environment first —
that is **not** evidence that the import pipeline is wrong.

**Operational notes:**

- `.scl` on disk for import should be **UTF-8 with BOM** (TIA expectation; same
  as user rule for generated SCL in this repo).
- `GetPlcExternalSources` returns names **with extension** (e.g. `Ramp.scl`).
  Pass the same string to `GenerateBlocksFromExternalSource`; the server also
  matches `MCPVerify_FC_SCL_v2` ↔ `MCPVerify_FC_SCL_v2.scl`.
- Re-importing the same file name fails with **“The name is not unique”** —
  call `DeletePlcExternalSource(softwarePath, name)` first (idempotent: OK if
  the source was never imported).
- For logic that still does not fit `PlcBuildAndImport` DSL (§10), prefer
  **external `.scl` + generate** or **UI authorship + `ExportBlock` +
  `ImportBlock`**; hand-writing `<StructuredText v4>` token XML is possible but
  extremely verbose.

## 15. Reusable libraries, master copies & types (泛用性 — reuse across projects)

Don't re-author the same UDT/FB/screen for every project. The roster already
exposes a reuse path; document and use it instead of regenerating.

| Need | Tool(s) | Notes |
|---|---|---|
| Inspect a `.al`/global library before pulling from it | `ProbeGlobalLibrary`, `AnalyzeGlobalLibraryPackage` | Read-only; lists master copies + types + folder layout |
| Plan which library items map to this project | `PlanGlobalLibraryTemplateReuse` | Returns a copy plan; review before importing |
| Pull a master copy (block/screen/faceplate) into the project | `ImportMasterCopyFromGlobalLibrary` | Lands a ready-made object — far cheaper than rebuilding |
| Move UDTs / data types between projects | `ExportType` / `ImportType` (`ExportTypes`/`GetTypes`/`GetTypeInfo` for bulk/inspect) | Types are the portable unit; export once, import everywhere |

**Workflow — adopt a company/vendor library into a new project:**
```
Bootstrap → AttachToOpenProject → GetProjectTree
ProbeGlobalLibrary(path=<.al>) → AnalyzeGlobalLibraryPackage   ← see what's inside
PlanGlobalLibraryTemplateReuse                                  ← review the copy plan
ImportMasterCopyFromGlobalLibrary(<item>)  / ImportType(<udt>)  ← pull, don't rebuild
CompileSoftware → SaveProject
```
This is the cheapest route to "make a rich project" when a curated library exists —
prefer it over `ScaffoldProject` regeneration when the user already has standards.

## 16. Offline diff & version control (Git/VCI parity)

Competitors (T-IA Connect VCI, Openness Manager) ship native Git + fingerprint
diff. This MCP has **no native Git**, but TIA objects can be rendered to **text**
and diffed under any VCS — that covers most of the real need.

| Need | Tool(s) | Output |
|---|---|---|
| Snapshot blocks as diffable text | `ExportBlocksAsDocuments` / `ExportAsDocuments` | SIMATIC SD `.s7dcl`/`.scl` text — line-diffable |
| Snapshot a whole PLC program | `ExportBlocks` + `ExportPlcTagTable` + `ExportTypes` | Re-importable XML/SD set |
| Compare project (offline) vs the CPU (online) | `CompareSoftwareToOnline` | Block-level differences |
| Find who references a tag/block | `GetCrossReferences` | Impact analysis before a change |

**Workflow — version a project in Git (text round-trip):**
```
ExportBlocksAsDocuments(softwarePath, outDir)   ← .s7dcl/.scl per block (UTF-8 BOM)
ExportTypes / ExportPlcTagTable                 ← types + tags alongside
<commit outDir to git>                          ← real, reviewable diffs over time
# restore/PR review: ImportBlocksFromDocuments / ImportType to re-materialize
```
Keep the export dir stable per project so commits diff cleanly. This gives
code-review and history without the licensed VCI feature.

## 17. Alarms & multilingual text

| Need | Tool(s) |
|---|---|
| Alarm classes (round-trip) | `ExportAlarmClasses` / `ImportAlarmClasses` |
| Alarm text lists | `ExportAlarmTextLists` / `ImportAlarmTextLists` |
| Per-instance alarm texts | `ExportAlarmInstanceTexts` |
| HMI text/graphic lists | `ExportAlarmTextLists` (HMI side) + Unified text-list tools |

Multilingual rule (consistent with §9a): block/network titles and comments live
as `MLC_*` IDs in the paired `.s7res` (`zh-CN:` + `en-US:`); HMI texts are
`MultilingualText` parts. For LAD imports, **add `en-US:` beside every `zh-CN:`**
or the SD import can fail (§9a boundary note). Round-trip alarm/text exports
through these tools rather than hand-editing project XML.

## 18. Capability boundary vs competitors (对标 — read before promising)

Honest scope so you don't over-promise. Quote this when a user asks "can it do X".

| Capability | This MCP | T-IA Connect | Openness Manager |
|---|---|---|---|
| Project/PLC/HMI create, blocks, tags, types | ✅ (`ScaffoldProject` one-shot) | ✅ | ✅ |
| SCL DSL + external-source generate | ✅ (§10/§14) | ✅ | ✅ |
| LAD authoring (S7DCL text) | ✅ verified (§9a) | ✅ (text LAD) | ✅ (S7DCL) |
| Unified HMI design JSON + bindings | ✅ (§12) | ✅ | ✅ (JSON screens) |
| OPC UA read (live values, monitoring) | ✅ read-only (`ReadPlcLiveValuesOpcUa`) | ✅ | ✅ browser+subscribe+trend |
| Download to CPU | ✅ fixed, verified on real CPU (§13) | ✅ | ✅ |
| **Safety F-block author / compile / signature** | ✕ **不做(主动放弃)** | ✅ full | ✅ full |
| **PLCSIM simulation / unit testing** | ✕ **不做(主动放弃)** | ✅ simulate | ✅ PLCSIM Advanced tests |
| Native Git / VCI | ✕ 不做(用文本导出替代 §16) | ✅ Git+CI | ✅ full Git UI |
| Block protection / encrypted vault | ❌ | partial | ✅ AES vault |
| UMAC user/rights, SiVArc auto-screens | ❌ | ✅ | partial |

**Where we win:** one-call `ScaffoldProject`, verified S7DCL LAD + mixed LAD/SCL,
honest verified-vs-not discipline, and being a real MCP (not a paid license).
**The ✕ rows are deliberate, not a backlog:** safety F-blocks / PLCSIM / native Git-VCI
are **intentionally out of scope** — low real benefit for this tool's job (engineering
automation) and risky on a live machine. Do **not** pitch them as "coming". Rationale in
`docs/server-maturity-roadmap.md` (bundle root).

## 19. Hard rules

1. **Never** call write tools before `Bootstrap` + `GetProjectTree`.
2. **Never** use a temporary/timestamped path on the user's real working
   project — use a separate scratch directory.
3. **Never** invent Openness reflection calls for items listed in §4.
4. **Always** end an editing session with `CompileSoftware` showing
   `errors=0` (warnings allowed) and `SaveProject` returning success.
5. **Always** quote Description tags exactly when filtering tools by layer
   (`[L0]`, `[L1]`, `[L2]`).
6. **For ladder, author S7DCL text (`.s7dcl` + `.s7res`, both UTF-8 *with* BOM)
   and import with `ImportBlocksFromDocuments`** (§9a). Do **not** hand-write FlgNet XML
   for contacts/coils/compare/math — the only XML LAD builder
   (`ComposePlcLadFcBlockXml`) does FC-call networks only.

If a step takes longer than 90 seconds with no output, stop. The most likely
cause is an Openness authorization dialog the user did not click. Report it,
do not loop.
