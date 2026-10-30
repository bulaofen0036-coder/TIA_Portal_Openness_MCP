# TIA Portal MCP 工具总览（全部 233 个）

> 由 `scripts/generate-tools-reference.py` 从源码 `[McpServerTool]` 属性生成，与运行时 `tools/list` 一致。生成日期：2026-10-26。
> 分层：**L0** 只读引导/诊断 · **L1** 高频主力 · **L2** 专用/离线构建器。
>
> **V19 注意**：`ExportAsDocuments`、`ExportBlocksAsDocuments`、`ImportFromDocuments`、`ImportBlocksFromDocuments` 在 V19 上返回 `NotSupportedOnVersion`（V19 没有 SIMATIC SD 文档 API）；5 个 VCI 工具不编译；默认 lite 档，全量工具用 `--profile full` 或 `TIA_MCP_PROFILE=full`。

## 索引

| 域 | 工具数 | 说明 |
|---|---:|---|
| [Bootstrap](#bootstrap) | 1 | 会话入口、状态、自检 |
| [Diagnostics](#diagnostics) | 5 | 环境与上下文校验 |
| [Exports](#exports) | 5 |  |
| [Guide](#guide) | 1 |  |
| [HMI](#hmi) | 22 | HMI 通用操作 |
| [HMI-Classic](#hmi-classic) | 8 | 经典面板（KTP/精智） |
| [HMI-Library](#hmi-library) | 6 | HMI 模板/全局库分析 |
| [HMI-Unified](#hmi-unified) | 24 | WinCC Unified |
| [Hardware](#hardware) | 31 | 设备、型号目录、PROFINET |
| [Meta](#meta) | 2 |  |
| [Online-Monitoring](#online-monitoring) | 12 | 只读在线监控、S7/OPC UA 读值 |
| [PLC-Alarms](#plc-alarms) | 5 | 报警类别与报警文本的导入导出 |
| [PLC-Builders](#plc-builders) | 9 | 离线 XML / DSL 构建器 |
| [PLC-Online](#plc-online) | 9 | 上线、下载、在线状态、在线/离线比较 |
| [PLC-OpcUA](#plc-opcua) | 4 | OPC UA 接口导入导出与启用开关 |
| [PLC-Software](#plc-software) | 54 | 块/类型/变量表/编译/外部源 |
| [PLC-TechnologyObjects](#plc-technologyobjects) | 3 | 工艺对象（TO）导入导出 |
| [Portal](#portal) | 6 | 连接、用户组、进程 |
| [Project](#project) | 10 | 工程创建/打开/保存/关闭/树 |
| [Reflection](#reflection) | 7 | 对象反射查询 |
| [Reports](#reports) | 6 | 验收/诊断/交付报告 |
| [Validation](#validation) | 3 | 离线校验套件 |
| **合计** | **233** | |

<a id="bootstrap"></a>
## Bootstrap（1 个）

### `Bootstrap` <sub>L0</sub>

[L0][Bootstrap] FIRST tool any AI model should call. Read-only single-call orientation: returns TIA version, Openness group status, current connection/project state, the recommended next tool, the L0/L1 tool roster, and known TIA Openness limitations. Does NOT connect to TIA Portal — call Connect afterwards based on RecommendedNextTool.

返回：`async Task<ResponseBootstrap>`

---

<a id="diagnostics"></a>
## Diagnostics（5 个）

### `Doctor` <sub>L0</sub>

[L0][Diagnostics] One-call environment doctor for non-experts. Checks TIA install, Openness group membership, and connection/project state, and returns a plain-language diagnosis with the exact fix per problem. When fix=true (default) it ENSURES Openness group membership (adds the current user; may prompt a Windows UAC dialog). Read-only apart from that one fix. Call this first when setup is failing or you are unsure the environment is ready.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `fix` | `bool` | `true` | when true (default), ensure Openness group membership (adds user, may prompt UAC). false = read-only diagnosis, no prompt. |

返回：`async Task<ResponseDoctor>`

### `RunCapabilitySelfTest` <sub>L0</sub>

[L0][Diagnostics]Run a read-only MCP/TIA readiness self-test. It checks Openness group membership, connection state, visible portal processes, optional automation context, and optional project tree readback without writing to the project.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `connectIfNeeded` | `bool` | `false` | When true, call Connect before checks if the server is not connected. This is read-only but attaches to TIA Portal. |
| `includeProjectTree` | `bool` | `false` | When true, include GetProjectTree output if a project is open. |
| `inspectPortalProcesses` | `bool` | `false` | When true, enumerate TIA Portal process/project details. This may attach to running TIA processes and can be slow on a contended workstation. |
| `expectedPlcSoftwarePath` | `string` | `"PLC_1"` | Expected PLC software path for ValidateAutomationContext. |
| `expectedHmiSoftwarePath` | `string` | `"HMI_RT_1"` | Expected HMI software path for ValidateAutomationContext. |

返回：`async Task<ResponseCapabilitySelfTest>`

### `RunHmiActionScriptRecipeSafetySelfTest` <sub>L2</sub>

[L2][Diagnostics]Offline-only helper: prove deterministic HMI button action scripts are allowed only for safe set/reset/toggle bit recipes, while high-risk writes and unverified navigation/popup recipes are blocked.

返回：`ResponseJsonReport`

### `RunOnlineMonitoringSafetySelfTest` <sub>L0</sub>

[L0][Diagnostics]Run a static, read-only safety self-test for online monitoring guardrails. It does not connect to TIA Portal, open projects, modify watch tables, write PLC values, or expose forced-value operations.

返回：`ResponseSafetySelfTest`

### `ValidateAutomationContext` <sub>L1</sub>

[L1][Diagnostics]Preflight current project for automation: devices, software, expected PLC/HMI paths, and project tree.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `expectedPlcSoftwarePath` | `string` | `"PLC_1"` | expected PLC software path, empty to skip |
| `expectedHmiSoftwarePath` | `string` | `"HMI_RT_1"` | expected HMI software path, empty to skip |

返回：`ResponseMessage`

---

<a id="exports"></a>
## Exports（5 个）

### `ClearExports` <sub>L1</sub>

[L1][Exports] Drop parked responses in bulk. NOTE: handles already expire on their own at 24h,

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `olderThanHours` | `int` | `24` | drop handles at least this old; 0 drops every handle |

返回：`ResponseMessage`

### `DeleteExport` <sub>L1</sub>

[L1][Exports] Drop one export handle once you are done with it. Optional — handles expire on

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `exportId` | `string` |  | the handle to drop |

返回：`ResponseMessage`

### `GetExport` <sub>L1</sub>

[L1][Exports] Read one page of a large response that was parked under an export handle.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `exportId` | `string` |  | the handle from a truncated response, e.g. 'ex_20260902103000_0001' |
| `offset` | `int` | `0` | character index to start at; use the previous page's nextOffset. 0 = beginning |
| `length` | `int` | `0` | how many characters to return. 0 or omitted = this session's response limit. |

返回：`ResponseMessage`

### `ListExports` <sub>L1</sub>

[L1][Exports] List the export handles currently held by this engine session — id, the tool

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `tool` | `string?` | `null` | optional filter, matches part of the producing tool's name |
| `limit` | `int` | `20` | maximum handles to return |

返回：`ResponseMessage`

### `SaveExport` <sub>L1</sub>

[L1][Exports] Write a parked response to a file in one step, instead of paging it through

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `exportId` | `string` |  | the handle from a truncated response |
| `outputPath` | `string` |  | full file path to write, e.g. 'C:\\Temp\\IO表.csv' |
| `raw` | `bool` | `false` | true = write the response text verbatim (JSON envelope included). Default false = write just the payload. |
| `overwrite` | `bool` | `false` | DEFAULT false — if the file already exists the call is REFUSED rather than replacing it. Pass true only after the user agreed to overwrite that specific file. |

返回：`ResponseMessage`

---

<a id="guide"></a>
## Guide（1 个）

### `GetAuthoringGuide` <sub>L0</sub>

[L0][Guide] Verified syntax + workflow cheat sheet for authoring TIA content through this server. CALL THIS BEFORE writing any SCL/LAD/DB/HMI content — it prevents the common encoding, syntax and tool-routing mistakes. Topics: workflow, scl, lad, db, hmi, errors. Read-only, does not touch TIA Portal.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `topic` | `string` |  | one of workflow \| scl \| lad \| db \| hmi \| errors |

返回：`ResponseMessage`

---

<a id="hmi"></a>
## HMI（22 个）

### `CompileAndDiagnoseHmi` <sub>L1</sub>

[L1][HMI] Compile an HMI and return structured errors/warnings, the HMI counterpart of CompileAndDiagnosePlc. Use it after generating screens/tags so you can read the diagnostics and fix them yourself instead of asking the engineer to compile in the TIA UI. WinCC Unified: HmiSoftware is not compilable on its own, so the owning device is compiled (same as the TIA UI does) and hardware diagnostics may appear alongside screen ones. Classic (Comfort/KTP): the HMI software itself is compiled. Requires: Connect + OpenProject. softwarePath from GetProjectTree, e.g. 'HMI_RT_1'.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | HMI software path, e.g. 'HMI_RT_1' |

返回：`ResponseCompileDiagnose`

### `DescribeHmiScreen` <sub>L2</sub>

[L2][HMI]Describe one HMI screen object (members/methods) by name under an HMI software.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | HMI software path, e.g. 'HMI_RT_1' |
| `screenName` | `string` |  | screen name, e.g. 'Main' |
| `maxMembers` | `int` | `200` | max member count |

返回：`ResponseObjectDescribe`

### `DescribeHmiScreenItem` <sub>L2</sub>

[L2][HMI]Describe one HMI screen item (widget) by name under an HMI screen.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | HMI software path, e.g. 'HMI_RT_1' |
| `screenName` | `string` |  | screen name, e.g. 'Main' |
| `itemName` | `string` |  | widget name, e.g. 'BTN_Start' |
| `maxMembers` | `int` | `200` | max member count |

返回：`ResponseObjectDescribe`

### `DescribeHmiSoftware` <sub>L2</sub>

[L2][HMI]Describe the HMI software object (members/methods) via reflection. Useful to discover Export/Import/Create APIs.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software (e.g. 'HMI_RT_1') |
| `maxMembers` | `int` | `200` | max member count |

返回：`ResponseObjectDescribe`

### `DescribeHmiTag` <sub>L2</sub>

[L2][HMI]Describe one HMI tag object (members/methods) by name under an HMI tag table.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | HMI software path, e.g. 'HMI_RT_1' |
| `tagTableName` | `string` |  | tag table name |
| `tagName` | `string` |  | tag name |
| `maxMembers` | `int` | `200` | max member count |

返回：`ResponseObjectDescribe`

### `DescribeHmiTagTable` <sub>L2</sub>

[L2][HMI]Describe one HMI tag table object (members/methods) by name under an HMI software.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | HMI software path, e.g. 'HMI_RT_1' |
| `tagTableName` | `string` |  | tag table name |
| `maxMembers` | `int` | `200` | max member count |

返回：`ResponseObjectDescribe`

### `ExportHmiConnection` <sub>L2</sub>

[L2][HMI]Export one HMI connection to a file (best-effort; Classic/Unified via reflection)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `connectionName` | `string` |  | the HMI connection name to export |
| `exportPath` | `string` |  | full file path to write to (e.g. C:\\temp\\connection.xml) |

返回：`ResponseExportFile`

### `ExportHmiProgram` <sub>L2</sub>

[L2][HMI]Batch export HMI screens/tagtables into a directory (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `exportDir` | `string` |  | directory to write exported files into |
| `exportScreens` | `bool` | `true` | default true |
| `exportTagTables` | `bool` | `true` | default true |

返回：`ResponseBatchExport`

### `ExportHmiScreen` <sub>L2</sub>

[L2][HMI]Export one HMI screen to a file (best-effort; requires Openness export support)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `screenName` | `string` |  | the screen name to export |
| `exportPath` | `string` |  | full file path to write to (e.g. C:\\temp\\screen.xml) |

返回：`ResponseExportFile`

### `ExportHmiTagTable` <sub>L2</sub>

[L2][HMI]Export one HMI tag table to a file (best-effort; requires Openness export support)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `tagTableName` | `string` |  | the tag table name to export |
| `exportPath` | `string` |  | full file path to write to (e.g. C:\\temp\\tagtable.xml) |

返回：`ResponseExportFile`

### `GetHmiConnections` <sub>L2</sub>

[L2][HMI]List HMI connection names (Classic/Unified, best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |

返回：`ResponseStringList`

### `GetHmiProgramInfo` <sub>L2</sub>

[L2][HMI] Get HMI software type (Classic/Basic/Unified), version, and list of all screen names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'HMI_RT_1'). Use to confirm HMI type before choosing Classic vs Unified tool variants.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software (see GetProjectTree) |

返回：`ResponseHmiProgramInfo`

### `GetHmiScreenItemDetails` <sub>L2</sub>

[L2][HMI] Read the PROPERTIES of every item on one HMI screen in one call: type (HmiButton/HmiIOField/HmiText/...), position, size, visibility, plus the screen's own size. Read-only. Screens inside a screen group are found. attributes: comma-separated list to override the default set (Name,Left,Top,Width,Height,Visible,Enabled); an attribute this item type does not have comes back null instead of failing the call. Requires: Connect + OpenProject. softwarePath from GetProjectTree, screenName from GetHmiScreens.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | HMI software path, e.g. 'HMI_RT_1' |
| `screenName` | `string` |  | screen name, e.g. 'MainScreen' |
| `attributes` | `string` | `""` | comma-separated attribute names; empty = the default set |

返回：`ResponseHmiScreenItemDetails`

### `GetHmiScreens` <sub>L2</sub>

[L2][HMI] List all screen names in an HMI (Classic or Unified). Requires: Connect + OpenProject. softwarePath from GetProjectTree. Use before EnsureUnifiedHmiScreen/ExportHmiScreen to confirm which screens exist.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |

返回：`ResponseStringList`

### `GetHmiTagDetails` <sub>L2</sub>

[L2][HMI] Read the PROPERTIES of HMI tags in one call: datatype, connection, address, PLC binding, acquisition cycle. Read-only. GetHmiTags gives names only; this gives values, for one tag table or (tagTableName empty) every tag table including the ones filed in a tag table group. attributes: comma-separated list to override the default set (Name,DataType,HmiDataType,Connection,Address,PlcTag,PlcName,AcquisitionCycle); an attribute this tag type does not have comes back null instead of failing the call. Requires: Connect + OpenProject. softwarePath from GetProjectTree, e.g. 'HMI_RT_1'.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | HMI software path, e.g. 'HMI_RT_1' |
| `tagTableName` | `string` | `""` | one tag table, or empty for every tag table of this HMI |
| `attributes` | `string` | `""` | comma-separated attribute names; empty = the default set |

返回：`ResponseHmiTagDetails`

### `GetHmiTagTables` <sub>L2</sub>

[L2][HMI]List HMI tag table names (Classic/Unified, best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |

返回：`ResponseStringList`

### `GetHmiTags` <sub>L2</sub>

[L2][HMI]List HMI tag names (best-effort). If tagTableName empty, returns tags found at root collection if available.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `tagTableName` | `string` | `""` | optional tag table name to list tags from |

返回：`ResponseStringList`

### `ImportHmiConnection` <sub>L2</sub>

[L2][HMI]Import one HMI connection XML file into an HMI program (best-effort; Classic/Unified via reflection)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `importPath` | `string` |  | full file path of exported HMI connection XML |

返回：`ResponseMessage`

### `ImportHmiScreen` <sub>L2</sub>

[L2][HMI]Import one HMI screen XML file into an HMI program (best-effort; Classic/Unified via reflection)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `folderPath` | `string` |  | optional screen group path inside HMI (use empty for root) |
| `importPath` | `string` |  | full file path of exported screen XML |

返回：`ResponseMessage`

### `ImportHmiScreensFromDirectory` <sub>L2</sub>

[L2][HMI]Batch import HMI screen .xml files from a directory (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `folderPath` | `string` |  | optional screen group path inside HMI (use empty for root) |
| `dir` | `string` |  | directory containing exported screen XML files |
| `regexName` | `string` | `""` | optional regex filter applied to filename without extension |
| `overwrite` | `bool` | `true` | true=Override (default) |

返回：`ResponseImportBatch`

### `ImportHmiTagTable` <sub>L2</sub>

[L2][HMI]Import one HMI tag table XML file into an HMI program (best-effort; Classic/Unified via reflection)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `folderPath` | `string` |  | optional tag table group path inside HMI (use empty for root) |
| `importPath` | `string` |  | full file path of exported tag table XML |

返回：`ResponseMessage`

### `ImportHmiTagTablesFromDirectory` <sub>L2</sub>

[L2][HMI]Batch import HMI tag table .xml files from a directory (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the HMI software |
| `folderPath` | `string` |  | optional tag table group path inside HMI (use empty for root) |
| `dir` | `string` |  | directory containing exported tag table XML files |
| `regexName` | `string` | `""` | optional regex filter applied to filename without extension |
| `overwrite` | `bool` | `true` | true=Override (default) |

返回：`ResponseImportBatch`

---

<a id="hmi-classic"></a>
## HMI-Classic（8 个）

### `BuildClassicHmiMinimalPackage` <sub>L2</sub>

[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package from structured JSON. It returns tag-table XML, screen XML, import order, and readiness checks that screen item tag references are declared in the tag table. It does not connect to TIA Portal, import files, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `packageJson` | `string` |  | JSON object with Name, ScreenDesign, and TagTable. Screen items may reference HMI tags through Tag/HmiTag/ProcessValueTag or Properties.*Tag. |

返回：`ResponseJsonReport`

### `BuildClassicHmiScreenXml` <sub>L2</sub>

[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI screen XML document from structured JSON. It does not connect to TIA Portal, import screens, or modify projects. Validate in a temporary Classic HMI project before using on a real project.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `designJson` | `string` |  | JSON object with Screen/Items. Items support Type=Text/Button/IOField/Lamp/Rectangle plus Name/Left/Top/Width/Height/Text/Properties. |

返回：`ResponseXmlBuild`

### `BuildClassicHmiTagTableXml` <sub>L2</sub>

[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI tag table XML document from structured JSON. Supports plain HMI tags and symbolic PLC bindings through Connection + ControllerTag/PlcTag. It does not connect to TIA Portal, import tags, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `tableJson` | `string` |  | JSON object with Name/TableName and Tags[]. Tag fields: Name, DataType, Length, optional Connection and ControllerTag/PlcTag. |

返回：`ResponseXmlBuild`

### `RunClassicHmiOfflineValidationSuite` <sub>L2</sub>

[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI validation suite covering PLC symbol extraction, HMI package generation, HMI tag references, and PLC-HMI sync positive/negative gates. It writes reports only to the requested report directory.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `reportDirectory` | `string` |  | directory where suite files and reports will be written. |

返回：`ResponseJsonReport`

### `RunClassicHmiTemporaryImportPreflight` <sub>L2</sub>

[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI temporary-import preflight. It checks TIA V21 environment, Openness group, package files, PLC-HMI sync, and emits an import/readback plan without connecting to TIA Portal or creating projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `workspaceRoot` | `string` |  | repository/workspace root. |
| `reportDirectory` | `string` |  | directory where preflight files and reports will be written. |

返回：`ResponseJsonReport`

### `ValidateClassicHmiMinimalPackageFiles` <sub>L2</sub>

[L2][HMI-Classic]Offline-only helper: validate an already written Classic/Basic HMI minimal package folder or manifest. It reads manifest/XML, checks parseability and HMI tag references, and does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `path` | `string` |  | package output directory or *_manifest.json path to validate. |

返回：`ResponseJsonReport`

### `ValidateClassicHmiMinimalPackagePlcSync` <sub>L2</sub>

[L2][HMI-Classic]Offline-only helper: validate that Classic/Basic HMI tag-table ControllerTag/PlcTag bindings exist in a caller-provided exact PLC symbol list. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `path` | `string` |  | package output directory or *_manifest.json path to validate. |
| `plcSymbolsJson` | `string` |  | JSON array of exact PLC symbols, or object with Symbols[]. Example: ["DB1_MotorData.Motor.Start"]. |

返回：`ResponseJsonReport`

### `WriteClassicHmiMinimalPackageFiles` <sub>L2</sub>

[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package and write tag-table XML, screen XML, and manifest JSON to an output directory. It does not connect to TIA Portal, import files, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `packageJson` | `string` |  | JSON object with Name, ScreenDesign, and TagTable. |
| `outputDirectory` | `string` |  | directory where tag-table XML, screen XML, and manifest JSON will be written. |

返回：`ResponseJsonReport`

---

<a id="hmi-library"></a>
## HMI-Library（6 个）

### `AnalyzeGlobalLibraryPackage` <sub>L2</sub>

[L2][HMI-Library]Analyze a TIA global library folder offline by file-system structure. It does not connect to TIA Portal, open the library, import content, or modify files.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `libraryPath` | `string` |  | global library folder path or .al* file path |

返回：`ResponseJsonReport`

### `AnalyzeHmiTemplateReference` <sub>L2</sub>

[L2][HMI-Library]Analyze local Unified HMI JSON templates against reference-project/runtime/global-library hints offline. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `templateDirectory` | `string` |  | directory containing Unified HMI JSON templates |
| `referenceProjectPath` | `string` |  | reference TIA project folder containing HMI runtime export/currentConfiguration |
| `referenceGlobalLibraryPath` | `string` |  | reference global library folder or .al* file |

返回：`ResponseJsonReport`

### `AnalyzeUnifiedHmiTemplateLayout` <sub>L2</sub>

[L2][HMI-Library]Offline-only QA for Unified HMI JSON templates. Checks theme metadata, screen bounds, duplicate item names, size issues, layout overlap warnings, density, and execution JSON shape. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `templateDirectory` | `string` |  | directory containing Unified HMI JSON templates |

返回：`ResponseJsonReport`

### `ImportMasterCopyFromGlobalLibrary` <sub>L2</sub>

[L2][HMI-Library] Import one MasterCopy from a TIA global library into a real Unified HMI screen and return ScreenItems readback evidence. This modifies the project, must be tried in a temporary project first, and reports failure unless the imported item is visible after readback.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `libraryPath` | `string` |  | full path to .al21 file or its containing folder |
| `masterCopyName` | `string` |  | exact or suffix path/name from ProbeGlobalLibrary MasterCopies readback |
| `hmiSoftwarePath` | `string` |  | real Unified HMI software path resolved from GetProjectTree, e.g. HMI_RT_1 |
| `screenName` | `string` |  | existing target Unified screen name; create it first with EnsureUnifiedHmiScreen if needed |
| `importedItemName` | `string` | `""` | optional expected item name after import; empty means use masterCopyName leaf |
| `left` | `int` | `0` | optional Left coordinate applied after import when supported |
| `top` | `int` | `0` | optional Top coordinate applied after import when supported |

返回：`ResponseGlobalLibraryImport`

### `PlanGlobalLibraryTemplateReuse` <sub>L2</sub>

[L2][HMI-Library] Plan the commercial fallback when direct MasterCopy import is not publicly verifiable: learn reference/global-library template evidence and rebuild screens with native Unified HMI MCP theme/layout/action tools. Offline planning only; it does not import library content or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `libraryPath` | `string` |  | reference global library folder path or .al* file path. |
| `templateIntentJson` | `string` | `"{}"` | optional JSON {"screenType":"overview","targetRuntime":"Unified","preferredComponents":[...]}. |

返回：`ResponseJsonReport`

### `ProbeGlobalLibrary` <sub>L2</sub>

[L2][HMI-Library]Open a TIA global library (.al21) read-only/best-effort and list accessible master copies/types/folders through public/reflection APIs. It does not import library content.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `libraryPath` | `string` |  | full path to .al21 file or its containing folder |
| `maxItems` | `int` | `500` | maximum items per list |

返回：`ResponseGlobalLibraryProbe`

---

<a id="hmi-unified"></a>
## HMI-Unified（24 个）

### `ApplyUnifiedHmiLayout` <sub>L2</sub>

[L2][HMI-Unified] Apply a grid layout to a real Unified HMI screen through ApplyUnifiedHmiScreenDesignJson. Requires a connected TIA project; verify changed items with DescribeHmiScreenItem/readback before saving.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `layoutJson` | `string` |  | JSON accepted by BuildUnifiedHmiLayoutDesignJson. |

返回：`ResponseMessage`

### `ApplyUnifiedHmiScreenDesignJson` <sub>L2</sub>

[L2][HMI-Unified] PREFERRED for natural-language HMI design. Apply a complete JSON layout spec to a screen in one call: screen size + multiple controls (Button/Rectangle/IOField/Text) with positions, text, and properties. Use item type "Text" (HmiText) for static text labels/titles/field captions — a Rectangle has no Text property and renders blank if given text. Requires: Connect + OpenProject + EnsureUnifiedHmiScreen. Better than calling EnsureUnifiedHmiScreenItem multiple times: one call places many controls (V21: 100 controls in about 9 s), while one call per control adds a full model round trip per control. Per item, besides name/type/left/top/width/height/text/properties, optional parts: font {Size, Weight, Name} e.g. {"Size": 28, "Weight": "Bold"} (Weight and Name take the TIA enum names, e.g. Normal/Bold, SiemensSans), content {...}, padding {...}. Read a value back with GetObjectProperty(objectKind='HmiScreenItem', objectPath='<hmiSoftware>:<screen>:<item>', propertyPath='Font.Size'). Use BuildUnifiedHmiLayoutDesignJson to generate the JSON from a grid description.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `designJson` | `string` |  | JSON object with optional screen properties and items array |
| `strict` | `bool` | `true` | true fails the tool when any property/text write fails; false keeps legacy best-effort behavior. |

返回：`ResponseMessage`

### `ApplyUnifiedHmiTheme` <sub>L2</sub>

[L2][HMI-Unified] Apply a theme/palette to a real Unified HMI screen through ApplyUnifiedHmiScreenDesignJson. Requires a connected TIA project; verify with DescribeHmiScreenItem/readback before saving.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `themeJson` | `string` |  | JSON accepted by BuildUnifiedHmiThemeDesignJson. |

返回：`ResponseMessage`

### `BindUnifiedHmiButtonPressedTag` <sub>L2</sub>

[L2][HMI-Unified]Bind a Unified HMI button PressedStateTags entry to an HMI tag (momentary press behavior, best-effort).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `buttonName` | `string` |  | HMI button item name |
| `tagName` | `string` |  | HMI tag name to write while pressed |

返回：`ResponseMessage`

### `BindUnifiedHmiTagDynamization` <sub>L2</sub>

[L2][HMI-Unified]Ensure a Unified HMI TagDynamization exists for an item property and bind it to an HMI tag.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `itemName` | `string` |  | HMI screen item name |
| `propertyName` | `string` |  | target property name, e.g. BackColor or Visible |
| `tagName` | `string` |  | HMI tag name used as the dynamic source |
| `dataType` | `string` | `"Bool"` | tag data type, e.g. Bool, Int, Real |
| `plcTag` | `string` | `""` | optional PLC tag/path |
| `address` | `string` | `""` | optional absolute address |

返回：`ResponseMessage`

### `BuildUnifiedHmiButtonActionScript` <sub>L2</sub>

[L2][HMI-Unified]Build a safe Unified HMI button action script from a high-level action recipe without connecting to TIA.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `actionKind` | `string` |  | set-bit, reset-bit, toggle-bit, open-popup, goto-screen, confirm-write |
| `eventType` | `string` |  | HmiButtonEventType value, e.g. Down (press), Up (release), Tapped — NOT Pressed/Released |
| `targetTag` | `string` | `""` | target HMI tag for set/reset/toggle actions |
| `targetScreen` | `string` | `""` | target screen for goto-screen actions |
| `targetPopup` | `string` | `""` | target popup for open-popup actions |

返回：`ResponseMessage`

### `BuildUnifiedHmiLayoutDesignJson` <sub>L2</sub>

[L2][HMI-Unified][Offline] Build ApplyUnifiedHmiScreenDesignJson-compatible JSON from a grid layout. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `layoutJson` | `string` |  | JSON {grid?,left?,top?,gap?,columns?,cellWidth?,cellHeight?,items:[{name,type?,row?,col?,rowSpan?,colSpan?,text?,properties?}]}. |

返回：`ResponseJsonReport`

### `BuildUnifiedHmiTemplateApplyDesignJson` <sub>L2</sub>

[L2][HMI-Unified]Offline-only helper: convert one Unified HMI template JSON file into the execution JSON accepted by ApplyUnifiedHmiScreenDesignJson, with layout QA attached. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `templateFile` | `string` |  | full path to a Unified HMI template JSON file |
| `fallbackWidth` | `int` | `800` | width used only if the template omits Screen.Width |
| `fallbackHeight` | `int` | `480` | height used only if the template omits Screen.Height |

返回：`ResponseJsonReport`

### `BuildUnifiedHmiTemplateApplyDesignManifest` <sub>L2</sub>

[L2][HMI-Unified]Offline-only helper: build a directory-level manifest for Unified HMI templates. It summarizes layout QA and execution-design readiness for every unified_*.json template without returning full apply payloads. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `templateDirectory` | `string` |  | directory containing Unified HMI JSON templates |
| `fallbackWidth` | `int` | `800` | width used only if a template omits Screen.Width |
| `fallbackHeight` | `int` | `480` | height used only if a template omits Screen.Height |

返回：`ResponseJsonReport`

### `BuildUnifiedHmiThemeDesignJson` <sub>L2</sub>

[L2][HMI-Unified][Offline] Build ApplyUnifiedHmiScreenDesignJson-compatible JSON from a theme/palette. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `themeJson` | `string` |  | JSON {name?, palette:{Page?,Surface?,Text?,Border?,...}} with TIA ARGB colors like 0xFFF4F6F8. |

返回：`ResponseJsonReport`

### `DescribeUnifiedHmiButtonEventScript` <sub>L2</sub>

[L2][HMI-Unified]Describe a Unified HMI button event handler Script property and its current object members/attributes.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `buttonName` | `string` |  | HMI button item name |
| `eventType` | `string` |  | enum value, e.g. Tapped, Down, Up |
| `maxMembers` | `int` | `200` | max member count |

返回：`ResponseObjectDescribe`

### `EnsureStartStopUnifiedHmi` <sub>L2</sub>

[L2][HMI-Unified] SHORTCUT for motor start/stop HMI. Ensures HMI_Connection_1 uses the correct PLC driver (1200/1500 vs 300/400 from CPU TypeIdentifier), 4 HMI tags (StartPB/StopPB/EStop/RunOut) with symbolic PLC binding, and a simple styled Main screen. Requires: Connect + OpenProject + PLC + Unified HMI. Call after EnsureUnifiedHmiScreen if you need a fixed screen size. Idempotent.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` | `"Main"` | target screen name (default 'Main') |
| `tagTableName` | `string` | `"默认变量表"` | target HMI tag table name (default '默认变量表') |
| `plcName` | `string` | `"PLC_1"` | PLC software path / device name for connection + tag mapping (default 'PLC_1') |
| `connectionName` | `string` | `"HMI_Connection_1"` | Unified HMI connection object name (default 'HMI_Connection_1') |

返回：`ResponseMessage`

### `EnsureUnifiedHmiButtonAction` <sub>L2</sub>

[L2][HMI-Unified]Generate and apply a deterministic Unified HMI button action. Only set-bit/reset-bit/toggle-bit are applied; high-risk or TODO recipes are rejected. SyntaxCheck is OFF by default (issue #36: it can crash TIA V21).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `buttonName` | `string` |  | HMI button item name |
| `eventType` | `string` |  | HmiButtonEventType value, e.g. Down (press), Up (release), Tapped — NOT Pressed/Released |
| `actionKind` | `string` |  | set-bit, reset-bit, or toggle-bit |
| `targetTag` | `string` |  | verified target HMI tag |
| `syntaxCheck` | `bool` | `false` | run TIA SyntaxCheck after writing the script. Default false - see SetUnifiedHmiButtonEventScriptCode. The applied recipes are generated by this server and already linted offline, so the check adds little and risks a V21 crash. |

返回：`ResponseMessage`

### `EnsureUnifiedHmiButtonEventHandler` <sub>L2</sub>

[L2][HMI-Unified]Ensure a Unified HMI button event handler exists and return its API shape. eventType must match HmiButtonEventType.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `buttonName` | `string` |  | HMI button item name |
| `eventType` | `string` |  | HmiButtonEventType value, e.g. Tapped, Down, Up; use ListUnifiedHmiApiTypes('HmiButtonEventType') to inspect |

返回：`ResponseMessage`

### `EnsureUnifiedHmiConnection` <sub>L2</sub>

[L2][HMI-Unified] Create or verify the PLC↔HMI communication connection (HMI_Connection_1 by default). Requires: Connect + OpenProject + both PLC and Unified HMI devices. Must exist before PLC-backed HMI tags can exchange data. TIA V21: a new connection is created through the hardware connection API — on Unified panels it is INTEGRATED (PLC partner set, symbolic plcTag binding works), and it also works on Classic/Comfort/Basic panels (KTP Basic, TP/KTP Comfort; classic tags then need absolute addresses). Both devices' Ethernet interfaces must be on the same subnet (ConnectDeviceNodesToProfinetSubnet), and an existing connection between the same HMI and PLC blocks a second one. If the hardware-level create fails on a Unified panel it falls back to a NON-integrated connection and says why. TIA V20: Unified only, NON-integrated (bind HMI tags with an absolute `address`, non-optimized DB); classic panels need the connection made in the TIA GUI. Also sets DisabledAtStartup=false and the PLC/HMI IPs from the hardware configuration (non-integrated), and fails if either cannot be applied.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `connectionName` | `string` | `"HMI_Connection_1"` | HMI connection name |
| `plcName` | `string` | `"PLC_1"` | PLC software/device symbolic name |

返回：`ResponseObjectDescribe`

### `EnsureUnifiedHmiDiscreteAlarm` <sub>L2</sub>

[L2][HMI-Unified]Create or update ONE WinCC Unified discrete alarm (HmiSoftware.DiscreteAlarms) and bind it to a bit of an existing HMI tag. Without this, a generated project ends up with an HmiAlarmControl on screen and Alm* bits in the tag table but an EMPTY alarm list - every step reports success and the alarm screen simply never shows anything at runtime. triggerTag must be an EXISTING HMI tag: a name that does not exist is REFUSED up front (with a sample of the real tag names) instead of being written as a dangling reference that only fails at compile or runtime. Also REFUSED before anything is written (meta.success=false, project untouched), because Openness accepts them and only the HMI compile fails: an alarmClass that does not exist, a trigger tag whose type is not Bool or a bit string (TIA V21 only accepts Bool / Byte / Word / DWord / LWord here: every integer type, signed or unsigned, and Real/String fail the HMI compile), a bit number outside the tag's width, and a tag+bit already used by another discrete alarm. Every property written is read back and compared; any item that cannot be read back, or reads back different, fails the call and says which one - the project may then be half-changed, which is reported rather than hidden. triggerMode accepts only the enum names this TIA version defines (typically OnRisingEdge / OnFallingEdge); an unknown value is rejected with the list of valid ones, never coerced. Requires: Connect + OpenProject; run CompileAndDiagnoseHmi and SaveProject afterwards.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | Unified HMI software path, e.g. 'HMI_RT_1' |
| `alarmName` | `string` |  | alarm name. An existing alarm with this name is updated, not duplicated. |
| `eventText` | `string` | `""` | the alarm text shown in the alarm control. Empty = leave unchanged. |
| `triggerTag` | `string` | `""` | name of an EXISTING HMI tag that carries the trigger bit; its data type must be Bool, Byte, Word, DWord or LWord (integer types such as Int/UInt/DInt are refused by the TIA compile). |
| `bitNumber` | `int` | `-1` | which bit of triggerTag raises the alarm (0-based). Bool tag: 0 only; Byte 0..7, Word 0..15, DWord 0..31, LWord 0..63. -1 (default) = leave unchanged (a new alarm gets bit 0). |
| `alarmClass` | `string` | `""` | name of an EXISTING HMI alarm class, e.g. 'Alarm' / 'Warning' (GetUnifiedHmiDiscreteAlarms lists them). Empty = leave the default. |
| `triggerMode` | `string` | `""` | OnRisingEdge or OnFallingEdge. Empty = leave the default. |

返回：`ResponseMessage`

### `EnsureUnifiedHmiDynamization` <sub>L2</sub>

[L2][HMI-Unified]Ensure a Unified HMI item property dynamization exists using a concrete dynamization type and return its API shape.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `itemName` | `string` |  | HMI screen item name |
| `propertyName` | `string` |  | target property name, e.g. BackColor or Visible |
| `dynamizationType` | `string` | `""` | type short name/full name; empty tries common candidates and returns errors if unsupported |

返回：`ResponseMessage`

### `EnsureUnifiedHmiScreen` <sub>L2</sub>

[L2][HMI-Unified] Create or verify a WinCC Unified HMI screen exists. Requires: Connect + OpenProject + Unified HMI. Idempotent. After creating a screen, add tags with EnsureUnifiedHmiTag, add controls with EnsureUnifiedHmiScreenItem, or apply a complete layout with ApplyUnifiedHmiScreenDesignJson.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `width` | `uint` | `0` | optional screen width, 0 means keep current |
| `height` | `uint` | `0` | optional screen height, 0 means keep current |

返回：`ResponseMessage`

### `EnsureUnifiedHmiScreenItem` <sub>L2</sub>

[L2][HMI-Unified] Create or verify a single Unified HMI control (button, lamp, IO field, etc.) on a screen. Requires: Connect + OpenProject + EnsureUnifiedHmiScreen. itemType: Button, Rectangle (lamp/indicator/background — has NO text), Text (static text label = HmiText), IOField (value display/entry), or full CLR type name. For a text caption/label ALWAYS use Text, never Rectangle (a Rectangle has no Text property and renders blank if given text). For a complete screen layout use ApplyUnifiedHmiScreenDesignJson instead.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `itemName` | `string` |  | screen item name |
| `itemType` | `string` | `"Button"` | Button, Rectangle/Lamp, IOField, or full CLR type name |
| `left` | `int` | `0` | X position |
| `top` | `int` | `0` | Y position |
| `width` | `uint` | `120` | item width |
| `height` | `uint` | `40` | item height |
| `text` | `string` | `""` | optional button/display text |

返回：`ResponseMessage`

### `EnsureUnifiedHmiTag` <sub>L2</sub>

[L2][HMI-Unified] Create or verify a Unified HMI external tag. For PLC-backed tags pass plcTag and address in the same call; the address must read back in Address/LogicalAddress, e.g. %DB200.DBX0.0. Requires: Connect + OpenProject + EnsureUnifiedHmiConnection + EnsureUnifiedHmiTagTable.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `tagTableName` | `string` |  | target HMI tag table name |
| `tagName` | `string` |  | HMI tag name |
| `hmiDataType` | `string` | `"Bool"` | HMI data type, e.g. Bool, Int, Real, String |
| `plcName` | `string` | `"PLC_1"` | PLC name for symbolic binding |
| `plcTag` | `string` | `""` | PLC tag name/path; empty means same as tagName |
| `connectionName` | `string` | `""` | HMI connection name; empty keeps current/auto |
| `address` | `string` | `""` | optional absolute PLC address, e.g. %DB200.DBX0.0. When supplied it is written as the verified HMI runtime address while plcTag remains available as the symbolic reference. |
| `requireVerifiedBinding` | `bool` | `true` | stable public generation should keep this true. Set false only for intentional internal HMI-only tags used by local validation/probes. |

返回：`ResponseMessage`

### `EnsureUnifiedHmiTagTable` <sub>L2</sub>

[L2][HMI-Unified] Create or verify a Unified HMI tag table exists. Requires: Connect + OpenProject + Unified HMI. Idempotent. Create tag tables before adding tags with EnsureUnifiedHmiTag. Default tag table name is '默认变量表'.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `tagTableName` | `string` |  | target HMI tag table name |

返回：`ResponseMessage`

### `GetUnifiedHmiDiscreteAlarms` <sub>L2</sub>

[L2][HMI-Unified]READ-ONLY. List every WinCC Unified discrete alarm with its trigger tag (RaisedStateTag), trigger bit (RaisedStateTagBitNumber), the absolute TriggerBitAddress TIA derived from them, alarm class, trigger mode, priority and the event text per language, plus the names of the alarm classes that actually exist in this HMI. Use it to read back what EnsureUnifiedHmiDiscreteAlarm wrote, and before adding alarms to see which tag/bit combinations are already taken (TIA refuses two alarms on the same tag+bit at compile time). A property that cannot be read is reported as '<read threw: ...>' and makes meta.success false, never an empty value. Nothing is written.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | Unified HMI software path, e.g. 'HMI_RT_1' |

返回：`ResponseMessage`

### `ListUnifiedHmiApiTypes` <sub>L2</sub>

[L2][HMI-Unified]List loaded WinCC Unified HMI API types/enums by name filter, useful for discovering event and dynamization types.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `nameContains` | `string` | `""` | case-insensitive substring filter, e.g. Dynamization or EventType |
| `limit` | `int` | `500` | max returned type lines |

返回：`ResponseStringList`

### `SetUnifiedHmiButtonEventScriptCode` <sub>L2</sub>

[L2][HMI-Unified]Set ScriptCode on a Unified HMI button event ScriptDynamization. SyntaxCheck is OFF by default because on TIA V21 it can crash the Portal process and lose the script (issue #36); pass syntaxCheck=true only when you need that evidence.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `hmiSoftwarePath` | `string` |  | path to HMI software (e.g. 'HMI_RT_1') |
| `screenName` | `string` |  | target screen name |
| `buttonName` | `string` |  | HMI button item name |
| `eventType` | `string` |  | enum value, e.g. Tapped, Down, Up |
| `scriptCode` | `string` |  | JavaScript code for the event |
| `globalDefinitionAreaScriptCode` | `string` | `""` | optional global definitions for the script |
| `async` | `bool` | `false` | whether the script is async |
| `syntaxCheck` | `bool` | `false` | run TIA SyntaxCheck after writing. Default false - on TIA V21 this call can crash the Portal process (NonRecoverableException) and lose the just-written script. When false, no syntaxErrorCount is reported: absence means NOT CHECKED, never zero errors. |

返回：`ResponseMessage`

---

<a id="hardware"></a>
## Hardware（31 个）

### `AddDevice` <sub>L2</sub>

[L2][Hardware] Add one hardware device using an exact MLFB/order number and version from the TIA hardware catalog. Requires: Connect + OpenProject. Use SearchHardwareCatalog first to find the exact MLFB/version. For unknown or approximate device names, use AddDeviceWithFallback or AddHardwareCatalogDeviceWithProbe instead.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `orderNumber` | `string` |  | MLFB/order number from the TIA hardware catalog, e.g. '6ES7211-1BE40-0XB0' or '6ES7 211-1BE40-0XB0' |
| `version` | `string` |  | catalog device version, e.g. 'V4.7', 'V3.1', or empty to let TIA try defaults |
| `deviceName` | `string` |  | name in project tree |

返回：`ResponseMessage`

### `AddDeviceWithFallback` <sub>L1</sub>

[L1][Hardware] PREFERRED for natural-language device insertion. Adds a Siemens hardware device by probing the installed TIA catalog with fallback versions. Requires: Connect + OpenProject. Example: 'add CPU 1211C AC/DC/RLY' → family='S7-1200'. Families: S7-1200, S7-1500, WinCCUnifiedPC. For third-party/GSD devices use AddGsdDeviceWithProbe.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `preferredMlfb` | `string` |  | preferred MLFB/order number, e.g. '6ES7211-1BE40-0XB0'; empty uses family fallback list |
| `preferredVersion` | `string` |  | preferred catalog version, e.g. 'V4.7'; empty probes known/default versions |
| `deviceName` | `string` |  | name in project tree |
| `family` | `string` | `"S7-1500"` | fallback hint, e.g. 'S7-1200', 'S7-1500', or 'WinCCUnifiedPC' |

返回：`ResponseDeviceProbe`

### `AddGsdDeviceWithProbe` <sub>L2</sub>

[L2][Hardware]Add a third-party GSD/GSDML hardware device by first searching the installed TIA hardware catalog, ranking candidates, and inserting with the exact catalog TypeIdentifier. Does not fall back to unrelated Siemens devices.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `keyword` | `string` |  | device/vendor/order/DAP keyword, e.g. 'AFM60A', 'ATV320', 'DL100' |
| `deviceName` | `string` |  | name in project tree, e.g. 'ENC_AFM60A_1' |
| `preferredDap` | `string` | `""` | optional DAP/version hint, e.g. 'DAP V2.0', 'DAP 1', or empty |

返回：`ResponseGsdDeviceProbe`

### `AddHardwareCatalogDeviceWithProbe` <sub>L2</sub>

[L2][Hardware]Add a hardware device by searching the installed TIA hardware catalog, ranking insertable TypeIdentifier candidates, and inserting the best match. Use for Siemens devices/HMI panels when exact TypeIdentifier is unknown, e.g. KTP700 Basic PN.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `keyword` | `string` |  | MLFB/order number/device/family text, e.g. 'KTP700 Basic PN', '6AV2123-2GB03', 'S7-1211C DC/DC/DC' |
| `deviceName` | `string` |  | name in project tree, e.g. 'HMI_KTP700_1' |
| `preferredText` | `string` | `""` | optional ranking hint, e.g. 'PN 17.0.0.0 landscape', '6AV2 123-2GB03-0AX0' |

返回：`ResponseHardwareCatalogDeviceProbe`

### `AttachDeviceNodeToSubnet` <sub>L2</sub>

[L2][Hardware] Attach one real device network node to an existing PROFINET subnet and return readback evidence. deviceItemPath must come from GetProjectTree/GetDeviceItemTree, interfaceIndex selects a discovered Industrial Ethernet/PROFINET node, and online/force operations are never used.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | real device/device-item path resolved from GetProjectTree/GetDeviceItemTree. |
| `interfaceIndex` | `int` |  | zero-based index among discovered Industrial Ethernet/PROFINET nodes under deviceItemPath. |
| `subnetName` | `string` |  | existing subnet name to attach to. |
| `anchorDeviceItemPath` | `string` | `""` | optional real device-item path used to EnsureSubnet first when subnetName is not found. |

返回：`ResponseMessage`

### `CompileHardware` <sub>L2</sub>

[L2][Hardware] Compile the HARDWARE configuration of one device (modules, addresses, network, firmware) and return TIA's errors/warnings - CompileSoftware / CompileAndDiagnosePlc do not cover this; use after PlugDeviceItem, IO-address or network changes. Requires: Connect + OpenProject, and the PLC offline (GoOffline first).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `devicePath` | `string` |  | device (station) name as listed by GetDevices, e.g. 'PLC_1' |

返回：`ResponseJsonReport`

### `ConnectDeviceNodesToProfinetSubnet` <sub>L1</sub>

[L1][Hardware] PREFERRED for PROFINET network setup. Finds the first IE/PROFINET node under two devices, creates/reuses a subnet on the first, connects the second, and returns readback evidence. Requires: Connect + OpenProject + both devices added. Typical: firstRootPath='PLC_1', secondRootPath='HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'. Verified for S7-1200 + KTP700 Basic PN.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `firstRootPath` | `string` |  | first device/device-item root, usually PLC root, e.g. 'PLC_1' |
| `secondRootPath` | `string` |  | second device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1' |
| `subnetName` | `string` | `"PN_IE_1"` | subnet name to create when the first node is not already connected, e.g. 'PN_IE_1' |

返回：`ResponseMessage`

### `DeleteDevice` <sub>L2</sub>

[L2][Hardware][Destructive] Preview or delete ONE device / station (PLC, HMI, IO device, drive). Defaults to dryRun=true = lists the device, its modules and software (block count), deletes nothing. Give the device name or its CPU/head-module name; a module name is refused. DANGER: deleting a PLC deletes its whole program; network, IO-system, S7 and HMI connections to it are cut. Verified after delete; then SaveProject.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `devicePath` | `string` |  | device (station) name as listed by GetDevices, or its CPU name, e.g. 'PLC_1' |
| `dryRun` | `bool` | `true` | true (default) only previews; false deletes and verifies |

返回：`ResponseJsonReport`

### `DumpDeviceAttributes` <sub>L2</sub>

[L2][Category:Hardware][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `devicePath` | `string` |  | device name, CPU/program name, or full name (e.g. 'S7-1200 station_3', '安全PLC', 'S7-1500/ET200MP station_1'). |
| `nameFilter` | `string?` | `null` | optional case-insensitive substring to narrow attribute names (e.g. 'protection'). Empty = all. |

返回：`ResponseJsonReport`

### `EnsureSubnet` <sub>L2</sub>

[L2][Hardware] Ensure an Industrial Ethernet/PROFINET subnet by anchoring on a real deviceItemPath from GetProjectTree/GetDeviceItemTree. Applies only through TIA Openness, then returns readback evidence (node path, subnet name, interface path). Does not guess paths.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `anchorDeviceItemPath` | `string` |  | real device/device-item path resolved from GetProjectTree/GetDeviceItemTree; used to create/reuse the subnet from its first PROFINET node. |
| `subnetType` | `string` |  | IndustrialEthernet/PROFINET/PN/IE only. |
| `subnetName` | `string` |  | exact subnet name to create or read back, e.g. PN_IE_1. |

返回：`ResponseMessage`

### `ExportDeviceAml` <sub>L2</sub>

[L2][Hardware] Read-only: export a device's hardware configuration to an AutomationML (CAx) .aml file. The file contains the configured IP address, subnet/mask, PROFINET device name and topology — info GetDeviceItemNetworkInfo omits. devicePath is the station/device path from GetProjectTree (e.g. 'S7-1200 station_3'). exportPath may be a folder (file named <device>.aml) or a full .aml path. Does NOT modify the project or go online.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `devicePath` | `string` |  | station/device path from GetProjectTree, e.g. 'S7-1200 station_3' |
| `exportPath` | `string` |  | target folder or full .aml file path |

返回：`ResponseExportDeviceAml`

### `GetDeviceInfo` <sub>L2</sub>

[L2][Hardware]Get info from a device from the current project/session

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `devicePath` | `string` |  | defines the path in the project structure to the device |

返回：`ResponseDeviceInfo`

### `GetDeviceIpAddress` <sub>L1</sub>

[L1][Category:Hardware][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `devicePath` | `string` |  | device name from GetProjectTree, e.g. 'PLC_1'. |

返回：`ResponseJsonReport`

### `GetDeviceItemInfo` <sub>L2</sub>

[L2][Hardware]Get info from a device item from the current project/session

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | defines the path in the project structure to the device item |

返回：`ResponseDeviceItemInfo`

### `GetDeviceItemIoAddresses` <sub>L2</sub>

[L2][Hardware] READ-ONLY. List the I/O addresses of one device item (signal module, signal board,

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | path in the project structure to the device item |

返回：`ResponseMessage`

### `GetDeviceItemNetworkInfo` <sub>L2</sub>

[L2][Hardware]Get network-related attributes for a device item (best-effort heuristic filter)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | path in the project structure to the device item |

返回：`ResponseNetworkInfo`

### `GetDeviceItemTree` <sub>L2</sub>

[L2][Hardware]Get a subtree view for a device item (hardware components + sub device items)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | path in the project structure to the device item |
| `maxDepth` | `int` | `4` | recursion depth, default 4 |

返回：`ResponseTree`

### `GetDevicePlugLocations` <sub>L2</sub>

[L2][Hardware] READ-ONLY. List the plug slots of one device item: which slot numbers are FREE

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | path to the host device item, e.g. 'PLC_1' for a CPU |

返回：`ResponseMessage`

### `GetDevices` <sub>L1</sub>

[L1][Hardware] List all hardware devices (PLCs, HMI panels, drives) in the project with their attributes. Requires: Connect + OpenProject. Prefer GetProjectTree for a visual overview; use this when you need the exact device Name and attribute values for subsequent operations like AddDevice or SetDeviceItemAttribute.

返回：`ResponseDevices`

### `GetProjectTopology` <sub>L1</sub>

[L1][Category:Hardware][PreCondition:Connect+OpenProject]

返回：`ResponseJsonReport`

### `GetPutGetAccess` <sub>L2</sub>

[L2][Category:Hardware][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `devicePath` | `string` |  | device name from GetProjectTree, e.g. 'PLC_1'. |

返回：`ResponseJsonReport`

### `PlanHardwareNetworkConfiguration` <sub>L2</sub>

[L2][Hardware][Offline] Validate a hardware network operation plan without connecting to TIA Portal or modifying a project. Use this before EnsureSubnet/AttachDeviceNodeToSubnet/SetCpuCommonSettings; rejects guessed paths, unsafe subnet types, invalid IP/mask/gateway, and CPU settings without exactAttributes.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `planJson` | `string` |  | JSON with operations[]. Supported operation types: EnsureSubnet, AttachDeviceNodeToSubnet, SetCpuCommonSettings. This is offline-only and performs validation only. |

返回：`ResponseJsonReport`

### `PlugDeviceItem` <sub>L2</sub>

[L2][Hardware][Destructive] Insert a SUBMODULE into an existing device: signal board (SB, e.g. SB 1221

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | host device item. A signal board plugs into the CPU itself, e.g. 'PLC_1' |
| `orderNumber` | `string` |  | MLFB of the module, e.g. '6ES7221-3BD30-0XB0' (with or without the space). A full 'OrderNumber:.../V1.1' type identifier is also accepted |
| `version` | `string` | `""` | module/firmware version, e.g. 'V1.1'. Leave empty to let TIA pick the default |
| `positionNumber` | `int` | `-1` | target slot. -1 (default) = pick the first free slot TIA accepts |
| `name` | `string` | `""` | name for the new module. Empty = auto-generated and de-duplicated against siblings |
| `dryRun` | `bool` | `true` | true (default) only runs the CanPlugNew feasibility check; set false to actually plug |

返回：`ResponseMessage`

### `ProbeHardwareHmiConnectionOwnerCandidates` <sub>L2</sub>

[L2][Hardware]Enumerate candidate owner objects for hardware HMI connection creation without calling GetService on each high-level object.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `plcRootPath` | `string` |  | PLC device/device-item root, e.g. 'PLC_1' |
| `hmiRootPath` | `string` |  | HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1' |
| `deepScan` | `bool` | `true` | when true include ancestor/project/device-level candidates; when false only direct node/interface/device-item candidates |

返回：`ResponseStringList`

### `ProbeHardwareHmiConnectionWhitelistedServices` <sub>L2</sub>

[L2][Hardware]Read-only scan of whitelisted services on safe hardware HMI connection owner candidates. Does not create connections and skips high-level project/composition objects to avoid hangs.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `plcRootPath` | `string` |  | PLC device/device-item root, e.g. 'PLC_1' |
| `hmiRootPath` | `string` |  | HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1' |
| `deepScan` | `bool` | `true` | when true include safe ancestors; when false only direct node/interface/device-item candidates |

返回：`ResponseStringList`

### `SearchHardwareCatalog` <sub>L1</sub>

[L1][Hardware]Search the installed TIA Portal hardware catalog for Siemens or third-party catalog entries by keyword, MLFB/order number, device family, or description. Use this before adding hardware when the exact TypeIdentifier is unknown, especially HMI panels such as KTP700 Basic.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `keyword` | `string` |  | MLFB/order number/device/family text, e.g. 'KTP700', '6AV2123', '1211C DC/DC/DC', '6ES7211-1AE40' |
| `limit` | `int` | `50` | maximum candidates to return |

返回：`ResponseHardwareCatalogSearch`

### `SearchInstalledGsdDevices` <sub>L2</sub>

[L2][Hardware]Search installed TIA V21 hardware catalog and local GSDML files for third-party PROFINET/GSD devices such as AFM60A, ATV320, or DL100. Use this before inserting a non-Siemens device.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `keyword` | `string` |  | device/vendor/order/DAP keyword, e.g. 'AFM60A', 'ATV320', 'DL100', 'SICK AFM60A' |
| `limit` | `int` | `50` | maximum candidates to return |

返回：`ResponseGsdDeviceSearch`

### `SetCpuCommonSettings` <sub>L2</sub>

[L2][Hardware] Set CPU common settings using exactAttributes JSON only after reading exact attribute names from GetDeviceItemInfo/GetDeviceItemNetworkInfo. The tool writes only those exact attributes, rejects missing/non-writable attributes, and returns readback evidence.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `cpuPath` | `string` |  | real CPU device-item path resolved from GetProjectTree/GetDeviceItemTree. |
| `settingsJson` | `string` |  | JSON object { "exactAttributes": { "ExactAttributeNameFromReadback": "value" } }. No aliases or guessed attribute names are accepted. |

返回：`ResponseMessage`

### `SetDeviceItemAttribute` <sub>L2</sub>

[L2][Hardware]Set one exact DeviceItem attribute by name with type coercion and detailed error return. Use GetDeviceItemInfo/GetDeviceItemNetworkInfo first.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | path in the project structure to the device item |
| `attributeName` | `string` |  | exact attribute name from GetDeviceItemInfo/GetDeviceItemNetworkInfo |
| `value` | `string` |  | new value as string; converted based on current attribute type |

返回：`ResponseMessage`

### `SetDeviceItemIoAddress` <sub>L2</sub>

[L2][Hardware][Destructive] Preview or change the I/O START ADDRESS of one device item

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `deviceItemPath` | `string` |  | path in the project structure to the device item, e.g. 'PLC_1/DI 8x24VDC_1' |
| `ioType` | `string` |  | Input, Output, Diagnosis or Substitute |
| `startAddress` | `int` |  | new start address as engine raw byte offset (%I2.0 -> 2) |
| `dryRun` | `bool` | `true` | true (default) only previews the change; set false to actually write it |

返回：`ResponseMessage`

### `SetPutGetAccess` <sub>L2</sub>

[L2][Category:Hardware][CONFIG-WRITE][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `devicePath` | `string` |  | device name from GetProjectTree, e.g. 'PLC_1'. |
| `enable` | `bool` | `true` | true to permit remote PUT/GET access, false to forbid it. |

返回：`ResponseJsonReport`

---

<a id="meta"></a>
## Meta（2 个）

### `CallTool` <sub>L0</sub>

[L0][Meta] Invoke a tool from the full roster by name, including ones not listed in this session.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `server` | `IMcpServer` |  |  |
| `context` | `RequestContext<CallToolRequestParams>` |  |  |
| `name` | `string` |  | exact tool name from FindTools, e.g. 'ExportPlcWatchTable'. |
| `argumentsJson` | `string` | `""` | JSON object of the tool's arguments, e.g. '{"softwarePath":"PLC_1"}'. Omit or '{}' for a no-argument tool. |
| `cancellationToken` | `CancellationToken` | `default` |  |

返回：`async Task<object?>`

### `FindTools` <sub>L0</sub>

[L0][Meta] Search the FULL tool roster, including tools not listed in this session.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `query` | `string` | `""` | space-separated words matched against tool names and descriptions, e.g. 'export watch table'. Empty lists the whole roster. |
| `limit` | `int` | `12` | max tools to return (default 12). Raise it for a broad survey. |

返回：`ResponseStringList`

---

<a id="online-monitoring"></a>
## Online-Monitoring（12 个）

### `GetPlcRunStateS7` <sub>L2</sub>

[L2][Online-Monitoring] Read-only: read the CPU operating mode (RUN / STOP / UNKNOWN) over the S7 protocol (port 102) — something TIA Openness cannot do. Also returns the CPU clock and a best-effort raw diagnostic-buffer dump (event IDs in hex + raw bytes; full event text needs TIA's text database, and SZL may be unavailable on some S7-1200 CPUs — reported cleanly). Never writes/forces/changes mode.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `ip` | `string` |  | CPU IP address, e.g. '192.168.0.32'. |
| `rack` | `int` | `0` | hardware rack. S7-1200/1500 = 0. |
| `slot` | `int` | `1` | hardware slot. S7-1200/1500 = 1. |
| `maxDiagEntries` | `int` | `20` | max diagnostic-buffer records to return (default 20; 0 = all returned by CPU). |
| `expectModuleContains` | `string` | `""` | optional identity guard substring (e.g. '1211C'). |

返回：`ResponseJsonReport`

### `MonitorWatchTableLiveS7` <sub>L2</sub>

[L2][Online-Monitoring] Read-only: live-monitor an existing TIA watch table. Reads the table's entry addresses (Openness, read-only) and live-reads them over the S7 protocol (port 102). Returns name/address/value rows. Never edits the watch table, writes, or forces. Absolute-address entries (e.g. %DB1.DBW0, %MW4) are read; symbolic/optimized entries are listed as unresolved (use OPC UA for those). Identity guard via expectModuleContains. Use GetPlcWatchTables to list table names.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. '安全PLC'. |
| `watchTableName` | `string` |  | existing watch table name from GetPlcWatchTables. |
| `ip` | `string` |  | CPU IP address, e.g. '192.168.0.32'. |
| `rack` | `int` | `0` | hardware rack. S7-1200/1500 = 0. |
| `slot` | `int` | `1` | hardware slot. S7-1200/1500 = 1. |
| `expectModuleContains` | `string` | `""` | optional identity guard (e.g. '1211C'); aborts on a positive module-type mismatch. |

返回：`ResponseJsonReport`

### `PlanOnlineReadOnlyDataProvider` <sub>L2</sub>

[L2][Online-Monitoring] Plan the commercial current-value path through an external read-only data provider such as opcua or s7-readonly. This is a preflight only: it does not connect, write PLC values, modify watch tables, go online/offline through TIA, or use force operations.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `provider` | `string` |  | opcua or s7-readonly. opcua is preferred for commercial symbolic readback. |
| `endpoint` | `string` |  | OPC UA endpoint URL or PLC endpoint/IP. It is validated only for shape and is not opened. |
| `tagPathsJson` | `string` |  | JSON array of declared symbolic PLC tags/DB members. Guessed M bits and unsafe intent names are rejected. |
| `optionsJson` | `string` | `"{}"` | optional JSON object such as {"pollMs":1000,"source":"watch-table-export"}. |

返回：`ResponseJsonReport`

### `PlanOnlineReadOnlyMonitoring` <sub>L2</sub>

[L2][Online-Monitoring] Validate an online-monitoring request shape without connecting to TIA Portal. Read-only preflight only: no go-online/offline, no watch-table modification, no value write, and no force operation.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path resolved from GetProjectTree/ValidateAutomationContext. |
| `tagPathsJson` | `string` |  | JSON array of symbolic PLC tag/member paths, for example ["DB_HMI.MotorRun","DB_HMI.SpeedSet"]. Do not pass guessed M bits. |
| `mode` | `string` | `"current-values"` | current-values or watch-table-export-plan. Both are read-only planning modes. |

返回：`ResponseJsonReport`

### `ProbePlcMonitorOnlineCapabilities` <sub>L2</sub>

[L2][Online-Monitoring]Read-only probe for PLC online/offline/watch/monitor API surfaces. It does not go online/offline, change watch tables, write values, or touch restricted safety APIs.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |

返回：`ResponseJsonReport`

### `ProbeS7CpuIdentity` <sub>L2</sub>

[L2][Online-Monitoring] Read-only: connect to a physical CPU over the S7 protocol (ISO-on-TCP, port 102) and read its identification (module type, serial, names). Does NOT write, force, or change CPU mode. Use this first to confirm the IP belongs to the intended PLC before reading values. S7-1200/1500 use rack 0, slot 1.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `ip` | `string` |  | CPU IP address, e.g. '192.168.0.32'. |
| `rack` | `int` | `0` | hardware rack number. S7-1200/1500 = 0. |
| `slot` | `int` | `1` | hardware slot number. S7-1200/1500 = 1; S7-300/400 often 2. |

返回：`ResponseJsonReport`

### `ReadPlcLiveValuesOpcUa` <sub>L2</sub>

[L2][Online-Monitoring] Read-only live values from a CPU's OPC UA server (default opc.tcp port 4840), independent of TIA Openness. Anonymous, no-security session; reads the Value attribute of each node. NEVER writes or calls methods. Node IDs use OPC UA syntax, e.g. 'ns=3;s="DB10"."X"' or 'i=2258'. Precondition: the CPU's OPC UA server must be enabled and the variables exposed (Runtime license on S7-1200/1500). If the server is off, connection is refused (reported cleanly).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `endpointUrl` | `string` |  | OPC UA endpoint, e.g. 'opc.tcp://192.168.0.10:4840'. |
| `nodeIdsJson` | `string` |  | JSON array or comma-separated list of OPC UA NodeIds, e.g. ["ns=3;s=\"DB10\".\"X\"","ns=3;s=\"DB10\".\"Y\""]. |
| `timeoutMs` | `int` | `5000` | overall read timeout in milliseconds. |

返回：`ResponseJsonReport`

### `ReadPlcLiveValuesS7` <sub>L2</sub>

[L2][Online-Monitoring] Read-only FAST live values from a physical CPU over the S7 protocol (port 102), independent of TIA Openness. Give absolute S7 addresses (DB10.DBD0:REAL, DB1.DBX2.3, M0.0, MW12, DB5.DBD8:DINT). Returns current values in one round-trip (typically tens of ms). NEVER writes/forces. Preconditions for S7-1200/1500: enable 'Permit PUT/GET access' on the CPU and read NON-optimized DBs (M/I/Q are unrestricted). Use expectModuleContains to hard-guard the target identity.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `ip` | `string` |  | CPU IP address, e.g. '192.168.0.32'. |
| `itemsJson` | `string` |  | JSON array or comma-separated list of absolute S7 addresses, e.g. ["DB10.DBD0:REAL","DB10.DBD4:REAL","M0.0"]. |
| `rack` | `int` | `0` | hardware rack. S7-1200/1500 = 0. |
| `slot` | `int` | `1` | hardware slot. S7-1200/1500 = 1. |
| `expectModuleContains` | `string` | `""` | optional identity guard. If set (e.g. '1211C'), the read aborts unless the CPU module type contains this substring. |
| `devicePath` | `string` | `""` | optional device name (e.g. 'PLC_1') for a PUT/GET precheck. When set, the read is skipped with a clear, actionable message if the CPU has remote PUT/GET access disabled, instead of failing with a TCP timeout. |

返回：`ResponseJsonReport`

### `ReadPlcWatchTableCurrentValuesReadOnly` <sub>L2</sub>

[L2][Online-Monitoring] Read current/monitor value properties from an existing PLC watch table only. It does not create/modify watch tables, write PLC values, go offline, or use force operations.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path resolved from GetProjectTree/ValidateAutomationContext. |
| `watchTableName` | `string` |  | existing PLC watch table path/name returned by GetPlcWatchTables. |
| `maxEntries` | `int` | `50` | maximum entries to inspect. |

返回：`ResponseJsonReport`

### `SamplePlcLiveValuesS7` <sub>L2</sub>

[L2][Online-Monitoring] Read-only TREND sampling over the S7 protocol (port 102): on one open connection, read a set of absolute S7 addresses every intervalMs for up to durationMs (hard-capped at 120 s / 5000 samples), returning a time series per address plus min/max/avg of the numeric ones. Ideal for capturing a PID step response or watching a signal move over time. NEVER writes/forces. The call BLOCKS for ~durationMs while it samples. Same address syntax and identity guard as ReadPlcLiveValuesS7.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `ip` | `string` |  | CPU IP address, e.g. '192.168.0.32'. |
| `itemsJson` | `string` |  | JSON array or comma-separated list of absolute S7 addresses, e.g. ["DB10.DBD0:REAL","M0.0"]. |
| `intervalMs` | `int` | `100` | sampling period in ms (clamped to >= 20). |
| `durationMs` | `int` | `5000` | total sampling window in ms (clamped to <= 120000). |
| `maxSamples` | `int` | `600` | hard cap on sample count (default 600, max 5000). |
| `rack` | `int` | `0` | hardware rack. S7-1200/1500 = 0. |
| `slot` | `int` | `1` | hardware slot. S7-1200/1500 = 1. |
| `expectModuleContains` | `string` | `""` | optional identity guard substring (e.g. '1211C'). |

返回：`ResponseJsonReport`

### `TraceTagCause` <sub>L2</sub>

[L2][Online-Monitoring] Answer 'why is tag X this value / what sets it' by static analysis of the OFFLINE project. Read-only: exports code blocks to SimaticML and finds every network that WRITES the tag (LAD coils S/R/=, or StructuredText ':=' assignments) plus the gating condition operands in those networks. Cross-reference service is not needed. Then live-read the returned gatingConditions with ReadPlcLiveValuesS7 to see which condition is currently driving the value. Tip: pass blockScope to limit which blocks are scanned (faster). NOTE: block export needs the project OFFLINE in TIA — Openness cannot export blocks while it is connected online to the PLC.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path from GetProjectTree, e.g. '安全PLC'. |
| `tag` | `string` |  | symbol to trace. Accepts a full path ('Crew_Data.Saddle_locationX') or a member name ('故障代码'). Quotes/whitespace are ignored. |
| `blockScope` | `string` | `""` | optional regex to limit scanned code blocks by name (e.g. 'FC\|Main'). Empty scans all code blocks. |

返回：`ResponseJsonReport`

### `TraceTagCauseLive` <sub>L2</sub>

[L2][Online-Monitoring] Live causal trace: runs the offline TraceTagCause to find what writes a tag and its gating conditions, then resolves each gating operand to an absolute address via the PLC tag table and LIVE-READS the resolvable ones over S7 (port 102) — so you see which interlock/condition is currently TRUE and driving the value. Read-only. Operands that are DB members / optimized / symbolic have no absolute PLC-tag address and are returned unresolved (read those via OPC UA). Requires the CPU ip; for the offline-only trace use TraceTagCause. NOTE: the offline block export needs the project OFFLINE in TIA (Openness cannot export in online mode); the S7 live-read is a separate direct connection, unaffected by going offline.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path from GetProjectTree, e.g. '安全PLC'. |
| `tag` | `string` |  | symbol to trace (full path or member name; quotes/whitespace ignored). |
| `ip` | `string` |  | CPU IP address for the live read, e.g. '192.168.0.32'. |
| `rack` | `int` | `0` | hardware rack. S7-1200/1500 = 0. |
| `slot` | `int` | `1` | hardware slot. S7-1200/1500 = 1. |
| `blockScope` | `string` | `""` | optional regex to limit scanned code blocks by name. Empty scans all. |
| `expectModuleContains` | `string` | `""` | optional identity guard substring (e.g. '1211C'). |

返回：`ResponseJsonReport`

---

<a id="plc-alarms"></a>
## PLC-Alarms（5 个）

### `ExportAlarmClasses` <sub>L2</sub>

[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `exportPath` | `string` |  | full file path for the export, e.g. 'C:\Temp\AlarmClasses.xml' |

返回：`ResponseMessage`

### `ExportAlarmInstanceTexts` <sub>L2</sub>

[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `exportPath` | `string` |  | full file path for the XLSX output |
| `includeInfoText` | `bool` | `true` | include the Info Text column (default: true) |
| `includeAdditionalTexts` | `bool` | `true` | include Additional Texts columns (default: true) |
| `includeAlarmClass` | `bool` | `true` | include the Alarm Class column (default: true) |

返回：`ResponseMessage`

### `ExportAlarmTextLists` <sub>L2</sub>

[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `exportPath` | `string` |  | full file path for the XLSX output, e.g. 'C:\Temp\AlarmTexts.xlsx' |

返回：`ResponseMessage`

### `ImportAlarmClasses` <sub>L2</sub>

[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `importPath` | `string` |  | full file path to import from |

返回：`ResponseMessage`

### `ImportAlarmTextLists` <sub>L2</sub>

[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `importPath` | `string` |  | full file path to the XLSX file |

返回：`ResponseMessage`

---

<a id="plc-builders"></a>
## PLC-Builders（9 个）

### `BuildFlgNetCallXml` <sub>L2</sub>

[L2][PLC-Builders][Offline] NARROW SCOPE: builds ONLY a LAD network that calls one FC with parameters. For general ladder (contacts/coils/SR/compare/Move/math) author S7DCL text and import with ImportBlocksFromDocuments — there is no XML builder for those, and hand-written FlgNet XML is the usual cause of import errors. Build a TIA V21 LAD FlgNet/v5 FC call network XML from structured JSON. Input: {callName,parameters:[{name,section,dataType,sourceKind?,symbolPath?|symbol?|value?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `flgNetJson` | `string` |  | JSON object with callName/name and parameters[]. Global parameters use symbolPath[] or dotted symbol; constants use sourceKind='constant' and value. |

返回：`ResponseXmlBuild`

### `BuildPlcGlobalDbXml` <sub>L2</sub>

[L2][PLC-Builders][Offline] Build a TIA V21 PLC GlobalDB XML document from structured JSON. Input: {dbName,dbNumber,staticMembers:[{name,datatype,externalWritable?,commentZhCn?,startValue?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `globalDbJson` | `string` |  | JSON object with dbName/name, dbNumber/number, and staticMembers[] or members[]. |

返回：`ResponseXmlBuild`

### `BuildPlcSymbolManifestFromXmlPath` <sub>L2</sub>

[L2][PLC-Builders]Offline-only helper: extract a PLC symbol manifest from PLC tag table and GlobalDB XML files or directories. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `path` | `string` |  | XML file or directory containing PLC tag table / GlobalDB XML exports. |

返回：`ResponseJsonReport`

### `BuildPlcTagTableXml` <sub>L2</sub>

[L2][PLC-Builders][Offline] Build a TIA V21 PLC tag table XML document from structured JSON. Input: {tableName,tags:[{name,dataTypeName,logicalAddress}]}. It only returns XML; it does not connect to TIA Portal, import tag tables, write files, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `tagTableJson` | `string` |  | JSON object with tableName/name and tags[]. Required tag fields: name, dataTypeName/datatype, logicalAddress/address. |

返回：`ResponseXmlBuild`

### `BuildPlcUdtXml` <sub>L2</sub>

[L2][PLC-Builders][Offline] Build a TIA V21 PLC UDT/PlcStruct XML document from structured JSON. Input: {members:[{name,datatype,externalWritable?,commentZhCn?}]}. It only returns XML; it does not connect to TIA Portal, import types, write files, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `udtJson` | `string` |  | JSON object with members[]. Required member fields: name, datatype. Optional: externalWritable, commentZhCn/comment. |

返回：`ResponseXmlBuild`

### `BuildStructuredTextXml` <sub>L2</sub>

[L2][PLC-Builders][Offline] Build a TIA V21 StructuredText/v4 XML fragment from operation JSON. Input: {operations:[{op:'if'|'else'|'endif'|'assignment'|'token'|'blank'|'newline', ...}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `structuredTextJson` | `string` |  | JSON object with operations[]. assignment uses target + literalValue/value; if uses condition/variable; token uses text. |
| `innerOnly` | `bool` | `false` | true returns only inner XML for embedding into a block composer; false returns <StructuredText>. |

返回：`ResponseXmlBuild`

### `ComposePlcFbBlockXml` <sub>L2</sub>

[L2][PLC-Builders][Offline] Compose a TIA V21 SCL FB block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs?,outputs?,inouts?,statics?,temps?,structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, create instance DBs, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `fbBlockJson` | `string` |  | JSON object with blockName/name, blockNumber/number, optional inputs/outputs/inouts/statics/temps arrays, and structuredTextInnerXml or structuredText.operations[]. |

返回：`ResponseXmlBuild`

### `ComposePlcFcBlockXml` <sub>L2</sub>

[L2][PLC-Builders][Offline] Compose a TIA V21 SCL FC block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs:[{name,datatype}],outputs:[{name,datatype}],structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `fcBlockJson` | `string` |  | JSON object with blockName/name, blockNumber/number, inputs[], outputs[], and structuredTextInnerXml or structuredText.operations[]. |

返回：`ResponseXmlBuild`

### `ComposePlcLadFcBlockXml` <sub>L2</sub>

[L2][PLC-Builders][Offline] NARROW SCOPE: every network must be an FC call; this cannot emit contacts/coils/SR/compare/Move/math. For general ladder, author S7DCL text (.s7dcl + .s7res) and import with ImportBlocksFromDocuments instead. Compose a TIA V21 LAD FC block XML containing one or more FlgNet/v5 FC-call networks. Each network is an FC call described as { callJson: { callName, parameters[] }, titleZhCn?, commentZhCn? }. Top-level: blockName, blockNumber, optional inputs/outputs members, optional commentZhCn / titleZhCn. Returns XML only; does not connect to TIA Portal or import. Pair with ImportBlock.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `ladFcBlockJson` | `string` |  | JSON object with blockName, blockNumber, networks[] (each with callJson{callName,parameters[]}, optional titleZhCn/commentZhCn), optional inputs[]/outputs[] interface members with commentZhCn, optional commentZhCn/titleZhCn block-level. |

返回：`ResponseXmlBuild`

---

<a id="plc-online"></a>
## PLC-Online（9 个）

### `CheckDownloadReadiness` <sub>L1</sub>

[L1][Category:PLC-Online][PreCondition:Connect+OpenProject+CompileSoftware]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software in the project tree, e.g. 'PLC_1' |

返回：`ResponseCheckDownload`

### `CompareSoftwareToOnline` <sub>L2</sub>

[L2][Category:PLC-Online][PreCondition:Connect+OpenProject+GoOnline]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `maxDepth` | `int` | `4` | maximum tree depth to walk (default 4). Lower = faster but less detail. |
| `maxEntries` | `int` | `200` | cap on differences returned (default 200). Truncated=true in response if reached. |

返回：`ResponseCompare`

### `DownloadToPlc` <sub>L1</sub>

[L1][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+CompileSoftware+CheckDownloadReadiness]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `consistentBlocksOnly` | `bool` | `true` | true=download only consistent blocks (safe default), false=download all blocks even inconsistent ones |
| `keepActualValues` | `bool` | `true` | true=preserve current DB actual values (safe default), false=reset all DB values to initial values (irreversible) |
| `startAfterDownload` | `bool` | `true` | true=automatically set CPU to RUN after download (default), false=leave CPU in STOP |
| `stopBeforeDownload` | `bool` | `true` | true=automatically stop CPU before download (required for most downloads), false=attempt online download without stopping |
| `password` | `string` | `""` | optional CPU access password. Required when the CPU has download protection configured. Leave empty for unprotected CPUs. |
| `pgPcInterface` | `string` | `""` | optional PG/PC interface name (substring, case-insensitive), e.g. 'PLCSIM' or 'Realtek'. Leave empty to auto-pick the adapter that shares a subnet with the CPU. Run CheckDownloadReadiness to see the available names. |
| `targetIpAddress` | `string` | `""` | optional CPU IP to download to, e.g. '192.168.0.1'. Disambiguates which route to use when the project has several CPU interfaces. Leave empty to auto-pick. |
| `abortActiveTests` | `bool` | `false` | DEFAULT false. When test/commissioning functions are active on the CPU, TIA asks whether to cancel them; false answers 'no action' and the download stops. true cancels them - ask the user first, somebody may be commissioning the machine. |

返回：`ResponseDownload`

### `GetOnlineState` <sub>L1</sub>

[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |

返回：`ResponseOnlineState`

### `GetPlcForceTables` <sub>L2</sub>

[L2][Category:PLC-Online][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |

返回：`ResponseStringList`

### `GoOffline` <sub>L1</sub>

[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |

返回：`ResponseMessage`

### `GoOfflineAll` <sub>L1</sub>

[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]

返回：`ResponseJsonReport`

### `GoOnline` <sub>L1</sub>

[L1][Category:PLC-Online][ONLINE-CONNECT][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `ipAddress` | `string` | `""` | optional target IP address, e.g. '192.168.1.10', used to pick the matching configured route. Must be one of the addresses the project configures. Leave empty to use the default route. |
| `password` | `string` | `""` | optional CPU access password. Required when the CPU has read/write protection configured. Leave empty for unprotected CPUs. |

返回：`ResponseOnlineState`

### `SetWatchTableModifyValue` <sub>L2</sub>

[L2][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+GoOnline]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `tableName` | `string` |  | name of the watch table to configure (created if not existing) |
| `address` | `string` |  | variable address, e.g. 'DB1.DBX0.0', '%M0.0', 'MyTag' |
| `modifyValue` | `string` |  | value to write, e.g. 'TRUE', '42', '3.14' |
| `trigger` | `string` | `"Permanent"` | when to apply the write — Permanent \| PermanentAtStart \| OnceOnlyAtStart \| PermanentAtEnd \| OnceOnlyAtEnd \| OnceOnlyAtStop (default: Permanent) |

返回：`ResponseMessage`

---

<a id="plc-opcua"></a>
## PLC-OpcUA（4 个）

### `ExportOpcUaInterface` <sub>L2</sub>

[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `interfaceName` | `string` |  | exact name of the interface to export |
| `exportPath` | `string` |  | full file path for the XML output, e.g. 'C:\Temp\OpcUa_Interface.xml' |
| `interfaceType` | `string` | `"ServerInterface"` | 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace' |

返回：`ResponseMessage`

### `GetOpcUaConfig` <sub>L2</sub>

[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |

返回：`ResponseJsonReport`

### `ImportOpcUaInterface` <sub>L2</sub>

[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `importPath` | `string` |  | full file path to the XML file |
| `interfaceType` | `string` | `"ServerInterface"` | 'ServerInterface' (default) or 'ReferenceNamespace' |

返回：`ResponseMessage`

### `SetOpcUaInterfaceEnabled` <sub>L2</sub>

[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `interfaceName` | `string` |  | exact name of the interface as shown in GetOpcUaConfig |
| `enabled` | `bool` |  | true to enable, false to disable |
| `interfaceType` | `string` | `"ServerInterface"` | 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace' |

返回：`ResponseMessage`

---

<a id="plc-software"></a>
## PLC-Software（54 个）

### `CompileAndDiagnosePlc` <sub>L1</sub>

[L1][PLC-Software] PREFERRED compile tool. Compiles PLC and returns structured errors/warnings by recursively walking CompilerResult.Messages (V20/V21 PublicAPI). Leaf diagnostics include Path + Description; optional Line/Column via GetAttribute when exposed. Requires: Connect + OpenProject.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `password` | `string` | `""` | optional safety password |

返回：`ResponseCompileDiagnose`

### `CompileSoftware` <sub>L1</sub>

[L1][PLC-Software] Compile all blocks in the PLC software. Requires: Connect + OpenProject. Returns basic success/failure. For structured error/warning details use CompileAndDiagnosePlc instead. Must compile before ExportBlock if any blocks are inconsistent. After adding new blocks via import, always compile to catch type/interface mismatches.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `password` | `string` | `""` | the password to access adminsitration, default: no password |

返回：`ResponseCompile`

### `CreateInstanceDB` <sub>L2</sub>

[L2][PLC-Software] Create a single-instance DB (背景 DB) for an existing FB directly - no XML, no dummy call. dbName defaults to '<FB>_DB'; dbNumber 0 = auto; groupPath = existing block group. Read back incl. InstanceOfName (Data.verified). Then call the FB with it, CompileAndDiagnosePlc and SaveProject.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `fbName` | `string` |  | the FB (name or group path), e.g. 'FB_Motor' |
| `dbName` | `string` | `""` | new instance DB name (empty = '<FB>_DB') |
| `dbNumber` | `int` | `0` | DB number, 0 = automatic |
| `groupPath` | `string` | `""` | existing block group (empty = root) |

返回：`ResponseJsonReport`

### `CreatePlcBlockGroup` <sub>L2</sub>

[L2][PLC-Software] Create a (nested) program-block group/folder, creating any missing parent groups along the path. Idempotent — returns ok if the group already exists. groupPath uses '/' separators relative to 'Program blocks' root, e.g. '01_手动控制/手动意图'. Requires: Connect + OpenProject. Use this to set up the 手动/手自动接口/自动 layer folders, then MoveBlockToGroup to organize blocks into them.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `groupPath` | `string` |  | '/'-separated group path under Program blocks, e.g. '01_手动控制/手动意图' |

返回：`ResponseMessage`

### `CreatePlcTagTable` <sub>L2</sub>

[L2][PLC-Software] Create one EMPTY PLC tag table (变量表), optionally inside an existing tag-table group. Names are unique per PLC; a clash is refused.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `tagTableName` | `string` |  | name of the new tag table |
| `groupPath` | `string` | `""` | existing tag-table group, e.g. 'IO' (empty = root) |

返回：`ResponseJsonReport`

### `CreatePlcWatchTable` <sub>L2</sub>

[L2][PLC-Software] Create one EMPTY watch table (监控表), optionally inside an existing watch-table group. A name clash is refused.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `watchTableName` | `string` |  | name of the new watch table |
| `groupPath` | `string` | `""` | existing watch-table group (empty = root) |

返回：`ResponseJsonReport`

### `DeletePlcBlock` <sub>L2</sub>

[L2][PLC-Software][Destructive] Preview or delete exactly one PLC block by its exact path, including

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software, e.g. 'PLC_1' |
| `blockPath` | `string` |  | exact block path, e.g. 'DB_Test' or 'GroupA/FB_Motor'. Regex and wildcards are rejected. |
| `dryRun` | `bool` | `true` | true (default) only resolves and reports the target; false performs Delete() and verifies the block is absent |

返回：`ResponseJsonReport`

### `DeletePlcExternalSource` <sub>L2</sub>

[L2][PLC-Software]Delete a PLC external source by name so ImportPlcExternalSource can replace it (idempotent). Name may include or omit .scl.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `externalSourceName` | `string` |  | name from GetPlcExternalSources (e.g. MCPVerify_FC_SCL_v3.scl) |

返回：`ResponseMessage`

### `DeletePlcTag` <sub>L2</sub>

[L2][PLC-Software][Destructive] Preview or delete ONE PLC tag. Defaults to dryRun=true (shows name/type/address/comment). DANGER: code using the symbol falls back to the absolute address or stops compiling, HMI bindings by name break silently, the comment is lost. Verified after delete; then CompileAndDiagnosePlc + SaveProject.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `tagName` | `string` |  | exact tag name |
| `tagTableName` | `string` | `""` | optional table to disambiguate |
| `dryRun` | `bool` | `true` | true (default) previews; false deletes and verifies |

返回：`ResponseJsonReport`

### `DeletePlcTagTable` <sub>L2</sub>

[L2][PLC-Software][Destructive] Preview or delete ONE PLC tag table (variable table / tag list)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software, e.g. 'PLC_1' |
| `tagTableName` | `string` |  | bare table name, or the group-qualified path from GetPlcTagTables (e.g. 'Drives/VFD tags'). Regex and wildcards are rejected. |
| `dryRun` | `bool` | `true` | true (default) only resolves the table and lists its contents; false performs Delete() and verifies the table is absent |

返回：`ResponseJsonReport`

### `DeletePlcType` <sub>L2</sub>

[L2][PLC-Software][Destructive] Preview or delete ONE PLC user data type (UDT / PlcType) by its exact

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software, e.g. 'PLC_1' |
| `typePath` | `string` |  | exact UDT path, e.g. 'UDT_Motor' or 'GroupA/UDT_Motor'. Regex and wildcards are rejected. |
| `dryRun` | `bool` | `true` | true (default) only resolves the type and reports its cross references; false performs Delete() and verifies the type is absent |

返回：`ResponseJsonReport`

### `DeletePlcWatchTable` <sub>L2</sub>

[L2][PLC-Software][Destructive] Preview or delete ONE watch table. Defaults to dryRun=true (path and entry count). Affects commissioning only, not the program - but entries, modify values and comments are lost (ExportPlcWatchTable first). Verified after delete; then SaveProject.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `watchTableName` | `string` |  | bare name or group-qualified path |
| `dryRun` | `bool` | `true` | true (default) only previews; false deletes and verifies |

返回：`ResponseJsonReport`

### `DescribeBlockLogic` <sub>L1</sub>

[L1][PLC-Software] Read a block's LOGIC as READABLE TEXT — the fast, accurate way to analyze LADDER (LAD) without hand-parsing FlgNet XML. For each LAD network it reconstructs the power flow as a boolean-ish expression (series = ' · ', parallel = ' + '), shows coils ( )/(S)/(R), MOVE/compare/timer boxes with their operands, and — critically — FLAGS any contact whose operand is a LITERAL CONSTANT with '⟨常量⟩' (a normally-open contact wired to FALSE silently disables its whole rung; this is nearly impossible to spot by eye). SCL/STL networks are rendered inline as code. Use this to understand or review LAD logic before editing. Requires: Connect + OpenProject + the block consistent (compile first if IsConsistent=false; export does not work in online mode — GoOffline first). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. '5T车' or 'PLC_1' (from GetProjectTree) |
| `blockPath` | `string` |  | fully qualified 'Group/Subgroup/Name' from GetSoftwareTree |

返回：`ResponseBlockLogic`

### `ExportAsDocuments` <sub>L2</sub>

[L2][PLC-Software] PREFERRED on V21+ for exporting one block. Exports a single program block to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML (ExportBlock). Requires TIA Portal V20 or newer.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `blockPath` | `string` |  | defines the path in the project structure to the block |
| `exportPath` | `string` |  | defines the path where to export the documents |
| `preservePath` | `bool` | `false` | preserves the path/structure of the plc software |

返回：`ResponseExportAsDocuments`

### `ExportBlock` <sub>L2</sub>

[L2][PLC-Software] Export one block to an XML file. Requires: Connect + OpenProject + block must be consistent (compile first if IsConsistent=false). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree — bare names return InvalidParams with suggestions. Pick the right tool: batch → ExportBlocks; readable SCL/.s7dcl text → ExportAsDocuments.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `blockPath` | `string` |  | full path to the block in the project structure, e.g. 'Group/Subgroup/Name' (single names are ambiguous) |
| `exportPath` | `string` |  | defines the path where to export the block |
| `preservePath` | `bool` | `false` | preserves the path/structure of the plc software |

返回：`ResponseExportBlock`

### `ExportBlocks` <sub>L2</sub>

[L2][PLC-Software] Export all (or regexName-filtered) blocks to a directory as SimaticML XML. Pick the right tool: readable SCL/.s7dcl text → ExportBlocksAsDocuments; a single block → ExportBlock.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `server` | `IMcpServer` |  |  |
| `context` | `RequestContext<CallToolRequestParams>` |  |  |
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `exportPath` | `string` |  | defines the path where to export the blocks |
| `regexName` | `string` | `""` | defines the name or regular expression to find the block. Use empty string (default) to find all |
| `preservePath` | `bool` | `false` | preserves the path/structure of the plc software |

返回：`async Task<ResponseExportBlocks>`

### `ExportBlocksAsDocuments` <sub>L2</sub>

[L2][PLC-Software] PREFERRED on V21+ for batch export. Exports multiple program blocks to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML. Requires TIA Portal V20 or newer.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `server` | `IMcpServer` |  |  |
| `context` | `RequestContext<CallToolRequestParams>` |  |  |
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `exportPath` | `string` |  | defines the path where to export the documents |
| `regexName` | `string` | `""` | defines the name or regular expression to find the block. Use empty string (default) to find all |
| `preservePath` | `bool` | `false` | preserves the path/structure of the plc software |

返回：`async Task<ResponseExportBlocksAsDocuments>`

### `ExportPlcTagTable` <sub>L2</sub>

[L2][PLC-Software]Export one PLC tag table (PlcTagTable) to XML file

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `tagTableName` | `string` |  | PLC tag table name |
| `exportPath` | `string` |  | full file path to write to |

返回：`ResponseExportFile`

### `ExportPlcWatchTable` <sub>L2</sub>

[L2][PLC-Software]Export one PLC watch/monitor table (PlcWatchTable) to XML file. Read-only against the TIA project.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `watchTableName` | `string` |  | PLC watch table name |
| `exportPath` | `string` |  | full file path to write to |

返回：`ResponseExportFile`

### `ExportPlcWatchTablesToDirectory` <sub>L2</sub>

[L2][PLC-Software]Export all PLC watch/monitor tables to XML files. Read-only against the TIA project.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `dir` | `string` |  | output directory |
| `regexName` | `string` | `""` | optional regex filter applied to table name |

返回：`ResponseImportBatch`

### `ExportType` <sub>L2</sub>

[L2][PLC-Software]Export a type from the plc software

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `exportPath` | `string` |  | defines the directory where to export the type; output file will be '<type name>.xml' |
| `typePath` | `string` |  | defines the path in the project structure to the type |
| `preservePath` | `bool` | `false` | preserves the path/structure of the plc software |

返回：`ResponseExportType`

### `ExportTypes` <sub>L2</sub>

[L2][PLC-Software]Export types from the plc software to path

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `server` | `IMcpServer` |  |  |
| `context` | `RequestContext<CallToolRequestParams>` |  |  |
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `exportPath` | `string` |  | defines the path where to export the types |
| `regexName` | `string` | `""` | defines the name or regular expression to find the block. Use empty string (default) to find all |
| `preservePath` | `bool` | `false` | preserves the path/structure of the plc software |

返回：`async Task<ResponseExportTypes>`

### `GenerateBlocksFromExternalSource` <sub>L2</sub>

[L2][PLC-Software]Generate blocks from a PLC external source by name (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `externalSourceName` | `string` |  | name from GetPlcExternalSources |

返回：`ResponseMessage`

### `GetBlockInfo` <sub>L2</sub>

[L2][PLC-Software] Get detailed info for one block (attributes, language, number, modification time). Requires: Connect + OpenProject. blockPath must be fully qualified: 'Group/Subgroup/BlockName' — get it from GetSoftwareTree or GetBlocksWithHierarchy. Returns: IsConsistent (false = must compile before export).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `blockPath` | `string` |  | defines the path in the project structure to the block |

返回：`ResponseBlockInfo`

### `GetBlocks` <sub>L2</sub>

[L2][PLC-Software] Get a flat list of all blocks in PLC software. Requires: Connect + OpenProject. Use GetBlocksWithHierarchy instead when you need group/folder paths for ExportBlock. Returns: block name, number, type (OB/FC/FB/GlobalDB/InstanceDB), programming language.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `regexName` | `string` | `""` | defines the name or regular expression to find the block. Use empty string (default) to find all |

返回：`ResponseBlocks`

### `GetBlocksWithHierarchy` <sub>L2</sub>

[L2][PLC-Software]Get a list of all blocks with their group hierarchy from the plc software.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |

返回：`ResponseBlocksWithHierarchy`

### `GetCrossReferences` <sub>L2</sub>

[L2][PLC-Software]Get cross references for a Step7 block/type (best-effort). Requires applicable object and Openness support.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `objectPath` | `string` |  | blockPath or typePath inside the PLC software |
| `objectKind` | `string` | `"Block"` | Block or Type |
| `filter` | `string` | `"AllObjects"` | CrossReferenceFilter enum name (e.g. AllObjects, ObjectsWithReferences, UnusedObjects) |

返回：`ResponseCrossReferences`

### `GetPlcExternalSources` <sub>L2</sub>

[L2][PLC-Software]List PLC external source names (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |

返回：`ResponseStringList`

### `GetPlcTagTables` <sub>L2</sub>

[L2][PLC-Software] List all PLC tag table names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). Use before ExportPlcTagTable to get exact table names, or before ImportPlcTagTable to check for conflicts.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |

返回：`ResponseStringList`

### `GetPlcTags` <sub>L1</sub>

[L1][PLC-Software][READ-ONLY] List PLC tags (name, type, address, comment, HMI flags) without an XML export. Filter by table, name substring and/or address prefix ('%Q' = every output). includeConstants adds user constants. Use to find what sits on an address or which addresses are free. Capped by maxTags (default 300, Meta.truncated). Requires: Connect + OpenProject (or AttachToOpenProject).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `tagTableName` | `string` | `""` | one table (bare or group-qualified); empty = all |
| `nameFilter` | `string` | `""` | case-insensitive name substring; empty = all |
| `addressFilter` | `string` | `""` | address prefix, e.g. '%Q', '%I0.', '%MW'; empty = all |
| `includeConstants` | `bool` | `false` | also list user constants |
| `maxTags` | `int` | `300` | cap (default 300, max 5000) |

返回：`ResponseJsonReport`

### `GetPlcWatchTables` <sub>L2</sub>

[L2][PLC-Software]List PLC watch/monitor table names (PlcWatchTable). Read-only.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |

返回：`ResponseStringList`

### `GetSoftwareInfo` <sub>L1</sub>

[L1][PLC-Software] Get PLC software properties (language, version, block counts). Requires: Connect + OpenProject. softwarePath comes from GetProjectTree (e.g. 'PLC_1'). Use GetSoftwareTree for the full block hierarchy.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |

返回：`ResponseSoftwareInfo`

### `GetSoftwareTree` <sub>L1</sub>

[L1][PLC-Software] Get the full PLC block/type/external-source hierarchy as ASCII tree. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). ALWAYS call before ExportBlock/ImportBlock to get exact group paths (e.g. 'Program blocks/FBs/FB_Motor'). Returns OB/FB/FC/GlobalDB/UDT/ExternalSource blocks with group hierarchy.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |

返回：`ResponseSoftwareTree`

### `GetSoftwareUnits` <sub>L1</sub>

[L1][PLC-Software] Read-only. List the PLC software units (Software units, S7-1500) of a PLC software and the blocks inside each unit. Openness reaches units only through PlcUnitProvider, so they are not children of the PLC software and GetSoftwareTree / InvokeService alone cannot enumerate them. Every block comes with a blockPath of the form 'Software units/<unit>/<group>/<block>' that ExportBlock, GetBlockInfo, DescribeBlockLogic and the other block tools accept as-is. Requires: Connect + OpenProject. softwarePath from GetProjectTree.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software name from GetProjectTree, e.g. 'PLC_1' |
| `includeBlocks` | `bool` | `true` | true lists every block in every unit (name, type, number, language, blockPath); false returns unit names and block counts only |

返回：`ResponseMessage`

### `GetTypeInfo` <sub>L2</sub>

[L2][PLC-Software]Get a type info from the plc software

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `typePath` | `string` |  | defines the path in the project structure to the type |

返回：`ResponseTypeInfo`

### `GetTypes` <sub>L2</sub>

[L2][PLC-Software]Get a list of types from the plc software

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `regexName` | `string` | `""` | defines the name or regular expression to find the block. Use empty string (default) to find all |

返回：`ResponseTypes`

### `ImportBlock` <sub>L1</sub>

[L1][PLC-Software] Import a single SimaticML XML block file into PLC software. Requires: Connect + OpenProject. importPath must be an absolute path to a .xml file. After import it reads back to confirm the block is present (Meta.verified); call CompileAndDiagnosePlc for full consistency. Pick the right tool: SCL/.s7dcl text → ImportFromDocuments; multiple XML files → ImportBlocksFromDirectory; a full exported program (UDTs+tags+blocks) → ImportPlcProgramFromDirectory; JSON-built blocks → PlcBuildAndImport.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `groupPath` | `string` |  | defines the path in the project structure to the group, where to import the block |
| `importPath` | `string` |  | defines the path of the xml file from where to import the block |

返回：`ResponseImportBlock`

### `ImportBlocksFromDirectory` <sub>L2</sub>

[L2][PLC-Software] Batch import PLC block .xml (SimaticML) files from a directory into a block group. Pick the right tool: SCL/.s7dcl text → ImportBlocksFromDocuments; a full mixed program with UDTs+tag tables+blocks auto-ordered → ImportPlcProgramFromDirectory; a single XML file → ImportBlock.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `groupPath` | `string` |  | defines the path in the project structure to the group, where to import blocks |
| `dir` | `string` |  | directory that contains block .xml files |
| `regexName` | `string` | `""` | optional regex filter applied to filename without extension |
| `overwrite` | `bool` | `true` | true=Override, false=Rename |

返回：`ResponseImportBatch`

### `ImportBlocksFromDocuments` <sub>L2</sub>

[L2][PLC-Software] PREFERRED on V21+ for batch import. Imports multiple program blocks from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `server` | `IMcpServer` |  |  |
| `context` | `RequestContext<CallToolRequestParams>` |  |  |
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `groupPath` | `string` |  | optional path within the PLC program where the blocks should be placed (empty for root) |
| `importPath` | `string` |  | directory containing the document files (.s7dcl/.s7res) |
| `regexName` | `string` | `""` | name or regular expression to select block files (empty for all) |
| `importOption` | `string` | `"Override"` | ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures) |

返回：`async Task<ResponseImportBlocksFromDocuments>`

### `ImportFromDocuments` <sub>L2</sub>

[L2][PLC-Software] PREFERRED on V21+ for importing one block. Imports a single program block from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer. After import it reads back to confirm the block is present (Meta.verified).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `groupPath` | `string` |  | optional path within the PLC program where the block should be placed (empty for root) |
| `importPath` | `string` |  | directory containing the document files (.s7dcl/.s7res) |
| `fileNameWithoutExtension` | `string` |  | name of the block file without extension |
| `importOption` | `string` | `"Override"` | ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures) |

返回：`ResponseImportFromDocuments`

### `ImportPlcExternalSource` <sub>L2</sub>

[L2][PLC-Software]Import one PLC external source file into a group (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `groupPath` | `string` |  | external source group path (use empty for root) |
| `filePath` | `string` |  | path to external source file (.scl, etc.) |

返回：`ResponseMessage`

### `ImportPlcProgramFromDirectory` <sub>L2</sub>

[L2][PLC-Software] HIGH-LEVEL batch import tool. Recursively scans a directory for PLC XML files, auto-classifies them as UDT/TagTable/Block, imports in correct dependency order (UDTs first, then tag tables, then blocks), and optionally compiles. Requires: Connect + OpenProject. Best for importing a full exported PLC program or a set of generated XML blocks.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `sourceDir` | `string` |  | root directory containing exported PLC XML files |
| `typeGroupPath` | `string` | `""` | PLC data type group path; use empty for root |
| `tagFolderPath` | `string` | `""` | PLC tag table group path; use empty for root |
| `technologyFolderPath` | `string` | `""` | PLC technology object group path; use empty for root |
| `blockGroupPath` | `string` | `""` | PLC block group path; use empty for root Program blocks |
| `regexName` | `string` | `""` | optional regex filter applied to file name without extension |
| `compileAfter` | `bool` | `true` | compile PLC software after imports |
| `stopOnImportFailure` | `bool` | `false` | skip remaining imports after first import failure |
| `dryRun` | `bool` | `false` | only classify and return discovered objects; do not import or compile |

返回：`ResponsePlcProgramImport`

### `ImportPlcTagTable` <sub>L1</sub>

[L1][PLC-Software]Import one PLC tag table XML file into PLC software (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `folderPath` | `string` |  | optional tag table group path (use empty for root) |
| `importPath` | `string` |  | full file path of PLC tag table XML |

返回：`ResponseMessage`

### `ImportPlcTagTablesFromDirectory` <sub>L2</sub>

[L2][PLC-Software]Batch import PLC tag table .xml files from a directory (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `folderPath` | `string` |  | optional tag table group path (use empty for root) |
| `dir` | `string` |  | directory containing PLC tag table XML files |
| `regexName` | `string` | `""` | optional regex filter applied to filename without extension |
| `overwrite` | `bool` | `true` | true=Override (default) |

返回：`ResponseImportBatch`

### `ImportTechnologyObject` <sub>L2</sub>

[L2][PLC-Software]Import one PLC Technology Object XML file into PLC software (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `folderPath` | `string` |  | optional technology object group path (use empty for root) |
| `importPath` | `string` |  | full file path of Technology Object XML |

返回：`ResponseMessage`

### `ImportTechnologyObjectsFromDirectory` <sub>L2</sub>

[L2][PLC-Software]Batch import PLC technology object .xml files from a directory (best-effort)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path in the project structure to the PLC software |
| `folderPath` | `string` |  | optional technology object group path (use empty for root) |
| `dir` | `string` |  | directory containing technology object XML files |
| `regexName` | `string` | `""` | optional regex filter applied to filename without extension |
| `overwrite` | `bool` | `true` | true=Override (default) |

返回：`ResponseImportBatch`

### `ImportType` <sub>L1</sub>

[L1][PLC-Software]Import a type from file into the plc software

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | defines the path in the project structure to the plc software |
| `groupPath` | `string` |  | defines the path in the project structure to the group, where to import the type |
| `importPath` | `string` |  | defines the path of the xml file from where to import the type |

返回：`ResponseImportType`

### `MoveBlockToGroup` <sub>L2</sub>

[L2][PLC-Software] Move/organize an existing block into a program-block group (found anywhere by exact name). Openness cannot reparent a block, so this exports the block, deletes it, and re-imports it into the target group (SIMATIC SD .s7dcl preferred, SimaticML XML fallback for STL/mixed-language). The block number and references are preserved. autoCreateGroup creates the target group path if missing. Requires: Connect + OpenProject + block consistent (compile first). After moving, call CompileAndDiagnosePlc to confirm 0 errors. Note: avoid moving OBs with event bindings via this round-trip.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `blockName` | `string` |  | exact block name to move (searched across all groups) |
| `targetGroupPath` | `string` |  | '/'-separated destination group under Program blocks, e.g. '02_手自动接口' |
| `autoCreateGroup` | `bool` | `true` | create the target group path if it does not exist (default true) |

返回：`ResponseMessage`

### `PlcBuildAndImport` <sub>L1</sub>

[L1][PLC-Software] MAIN tool for creating new PLC blocks from natural language. Build one PLC artifact (UDT/tag table/GlobalDB/FC/FB) from structured JSON, then optionally import and compile. Use dryRun=true first to validate. Workflow: describe block in JSON → dryRun → review → dryRun=false to import. Replaces the multi-step Build*Xml + ImportBlock sequence.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1'. Required only when dryRun=false. |
| `kind` | `string` |  | udt\|tagtable\|globaldb\|fc\|fb |
| `json` | `string` |  | structured JSON matching the corresponding BuildPlc* tool. |
| `typeGroupPath` | `string` | `""` | PLC data type group path for kind=udt. |
| `tagFolderPath` | `string` | `""` | PLC tag table group path for kind=tagtable. |
| `blockGroupPath` | `string` | `""` | PLC block group path for kind=globaldb\|fc. |
| `compileAfter` | `bool` | `true` | compile PLC after import when dryRun=false. |
| `dryRun` | `bool` | `true` | true builds XML and returns the import plan without importing/compiling. |

返回：`ResponsePlcProgramImport`

### `RenamePlcObject` <sub>L2</sub>

[L2][PLC-Software] Rename a PLC block (and/or renumber), a UDT, a tag table, a watch table or a device; kind = block | type | tagtable | watchtable | device. Clashes refused first; read back (Data.verified). TIA updates calls in the project; exported files, HMI scripts and hard-coded DB numbers do not follow. V20 cannot rename watch tables. Then CompileAndDiagnosePlc + SaveProject.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `kind` | `string` |  | block \| type \| tagtable \| watchtable \| device |
| `objectPath` | `string` |  | current name or path (tables: group-qualified allowed; device: name) |
| `newName` | `string` | `""` | new name (empty = keep, e.g. when only renumbering) |
| `newNumber` | `int` | `0` | new block number (blocks only, 0 = keep) |
| `softwarePath` | `string` | `""` | PLC software path, e.g. 'PLC_1' (not for kind=device) |

返回：`ResponseJsonReport`

### `RepairAndReimportBlock` <sub>L2</sub>

[L2][PLC-Software]Try import a block XML; if compile fails, return diagnostics and best-effort suggestions (no destructive actions).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `importPath` | `string` |  | block XML path |
| `groupPath` | `string` | `""` | block group path; use empty for root Program blocks |
| `compileAfter` | `bool` | `true` | compile PLC after import |

返回：`ResponseRepairAndCompile`

### `SeedProjectFromReference` <sub>L2</sub>

[L2][PLC-Software]Seed PLC blocks/types and HMI screens/tagtables from a reference directory (manifest.json + {{PLACEHOLDER}} replace)

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `plcSoftwarePath` | `string` |  | path in the project structure to the PLC software |
| `hmiSoftwarePath` | `string` |  | path in the project structure to the HMI software |
| `referenceDir` | `string` |  | directory containing manifest.json and subfolders (plc/blocks, plc/types, hmi/screens, hmi/tags) |
| `placeholders` | `JsonObject?` | `null` | JsonObject key-values for replacement in XML, e.g. {"PLC_NAME":"PLC_1"} |

返回：`ResponseSeed`

### `SetPlcTag` <sub>L2</sub>

[L2][PLC-Software] Create or modify ONE PLC tag in place. Existing tag (any table): change newName / dataType / address / comment; omitted fields stay. Missing tag: created in tagTableName with dataType + address. Tag names are unique per PLC; a clash is refused. Read back (Data.verified); warns when the address is shared. Requires: Connect + OpenProject (or AttachToOpenProject). Then CompileAndDiagnosePlc + SaveProject.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | PLC software path, e.g. 'PLC_1' |
| `tagName` | `string` |  | tag to modify, or name of the tag to create |
| `tagTableName` | `string` | `""` | table for a NEW tag (bare or group-qualified); optional for an existing tag |
| `newName` | `string` | `""` | rename an existing tag (empty = keep) |
| `dataType` | `string` | `""` | e.g. 'Bool', 'Int', 'Real' (empty = keep; required to create) |
| `address` | `string` | `""` | e.g. '%I0.0', '%Q4.2', '%MW100' (empty = keep; required to create) |
| `comment` | `string` | `""` | comment text (empty = keep) |
| `commentLanguage` | `string` | `""` | culture, e.g. 'zh-CN' (empty = zh-CN, else first project language) |

返回：`ResponseJsonReport`

### `WritePlcSclSourceFile` <sub>L1</sub>

[L1][PLC-Software][Offline] Write SCL source text to a local .scl external-source file (UTF-8 WITH BOM, so Chinese comments are not imported as mojibake/乱码). This tool does NOT connect to TIA Portal and does NOT import anything — it only writes the file to disk and returns the path plus manual-import instructions. Use it as the robust fallback when XML block import is rejected (e.g. a TIA V20 portal rejecting V21 SimaticML tokens: 'Cannot create SW.Blocks.CompileUnit... token not supported'): the user imports the .scl manually in TIA via project tree → 'External source files' → 'Add new external file', then right-clicks the source → 'Generate blocks from source'. The sclContent must be a complete source, e.g. FUNCTION_BLOCK "Name" ... END_FUNCTION_BLOCK.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `sclContent` | `string` |  | the full SCL source text (complete FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / TYPE declarations). This is written verbatim. |
| `outputPath` | `string` | `""` | target .scl file path. If a directory is given (or the path has no extension), the file is named after the first block found in the source. Empty means a temp file under %TEMP%\tia_mcp_scl. |

返回：`ResponseMessage`

---

<a id="plc-technologyobjects"></a>
## PLC-TechnologyObjects（3 个）

### `ExportTechnologyObject` <sub>L2</sub>

[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `toName` | `string` |  | exact name of the technology object, e.g. 'Axis_1' |
| `exportPath` | `string` |  | full file path for the XML output, e.g. 'C:\Temp\Axis_1.xml' |

返回：`ResponseMessage`

### `ExportTechnologyObjectsToDirectory` <sub>L2</sub>

[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |
| `exportDir` | `string` |  | directory to write XML files to, e.g. 'C:\Temp\TOs' |
| `regexName` | `string` | `""` | optional regex filter on TO name; empty = export all |

返回：`ResponseImportBatch`

### `GetTechnologyObjects` <sub>L2</sub>

[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `softwarePath` | `string` |  | path to the PLC software, e.g. 'PLC_1' |

返回：`ResponseTechnologyObjectList`

---

<a id="portal"></a>
## Portal（6 个）

### `Connect` <sub>L1</sub>

[L1][Portal] Connect to a running TIA Portal instance or start a new one. MUST be the first tool called in every session. On success, state becomes Connected=true. If TIA Portal is not installed or the user is not in the 'Siemens TIA Openness' Windows group, this will fail — run EnsureOpennessUserGroup first.

返回：`ResponseConnect`

### `ConnectIsolated` <sub>L1</sub>

[L1][Portal] Start a BRAND-NEW headless TIA Portal instance instead of attaching to a running one.

返回：`ResponseConnect`

### `Disconnect` <sub>L1</sub>

[L1][Portal] Disconnect from TIA Portal and release the Openness handle. Call after all project work is done. Any unsaved changes will be lost — call SaveProject first if needed.

返回：`ResponseDisconnect`

### `EnsureOpennessUserGroup` <sub>L1</sub>

[L1][Portal]Ensure current Windows user is in TIA Openness user group (may prompt UI). Returns success=true when membership is OK.

返回：`async Task<ResponseMessage>`

### `GetState` <sub>L0</sub>

[L0][Portal] Get current connection state: IsConnected, open Project name, and open Session name. Use this to check preconditions before other tools — if IsConnected=false, call Connect first; if Project is empty, call OpenProject or CreateProject.

返回：`ResponseState`

### `ListPortalProcessProjects` <sub>L1</sub>

[L1][Portal]List running TIA Portal processes and the projects/sessions visible in each process.

返回：`ResponseStringList`

---

<a id="project"></a>
## Project（10 个）

### `ArchiveProject` <sub>L1</sub>

[L1][Project] Archive the open project into one file (.zap20/.zap21, TIA 'Archive...') for backup or hand-over; the project itself is unchanged. Must be saved first (unsaved changes are refused - SaveProject; the engine never saves on its own). mode: compressed (default) | none | discard | discardcompressed. Existing archives are never overwritten.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `targetDirectory` | `string` |  | folder for the archive (created if missing) |
| `archiveName` | `string` | `""` | file name without extension (empty = '<project>_yyyyMMdd_HHmm') |
| `mode` | `string` | `"compressed"` | compressed (default) \| none \| discard \| discardcompressed |

返回：`ResponseJsonReport`

### `AttachToOpenProject` <sub>L1</sub>

[L1][Project]Attach MCP to an already-open TIA Portal project by name (avoids disposed project handles).

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `projectName` | `string` |  | name shown in TIA (e.g. '项目1') |

返回：`ResponseMessage`

### `CloseProject` <sub>L1</sub>

[L1][Project] Close the currently open project or multi-user session. Requires: Connect + OpenProject. Any unsaved changes are lost — call SaveProject first. After closing, the connection remains active but no project is open. A project this session did not open (the user's own, attached from a running TIA Portal) is NOT closed unless closeForeignProject=true.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `closeForeignProject` | `bool` | `false` | DEFAULT false. If the open project belongs to the user (this session did not open it), the call is REFUSED rather than closing their work. Only pass true after the user has agreed to close it. |

返回：`ResponseCloseProject`

### `CreateProject` <sub>L1</sub>

[L1][Project] Create a new empty TIA Portal project. Requires: Connect. After creation, call AddDevice to add PLCs/HMIs, then GetProjectTree to verify. The project is automatically opened after creation — no separate OpenProject call needed.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `directoryPath` | `string` |  | folder where project will be created |
| `projectName` | `string` |  | project name |
| `closeForeignProject` | `bool` | `false` | DEFAULT false. If TIA already has a project open that this session did not open, the call is REFUSED rather than closing the user's work. Only pass true after the user has agreed to close it. |

返回：`ResponseMessage`

### `GetProject` <sub>L1</sub>

[L1][Project] List all open local projects and multi-user sessions with their attributes. Requires: Connect. Use this to confirm which project is active, or to find the project name for AttachToOpenProject.

返回：`ResponseGetProjects`

### `GetProjectTree` <sub>L1</sub>

[L1][Project] Get the full project device/software tree as ASCII art. Requires: Connect + OpenProject. ALWAYS call this first after opening a project to discover the exact softwarePath (e.g. 'PLC_1') and device paths needed by all other PLC/HMI/hardware tools. Returns device names, software nodes, and HMI nodes.

返回：`ResponseProjectTree`

### `OpenProject` <sub>L1</sub>

[L1][Project] Open a local TIA Portal project (.apXX) or multi-user session (.alsXX) file, where XX is the TIA version number (e.g. .ap21, .als21). Requires: Connect. Closes any currently open project first. After success, call GetProjectTree to explore its structure.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `path` | `string` |  | defines the path where to the project/session |
| `closeForeignProject` | `bool` | `false` | DEFAULT false. If TIA already has a project open that this session did not open, the call is REFUSED rather than closing the user's work. Only pass true after the user has agreed to close it. |

返回：`ResponseOpenProject`

### `SaveAsProject` <sub>L2</sub>

[L2][Project]Save current TIA-Portal project/session with a new name

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `newProjectPath` | `string` |  | defines the new path where to save the project |

返回：`ResponseSaveAsProject`

### `SaveProject` <sub>L1</sub>

[L1][Project] Save the currently open project or session to disk. Requires: Connect + OpenProject. Call after any significant change (device add, block import, HMI edit). Compile first if there are pending changes to ensure consistency.

返回：`ResponseSaveProject`

### `ScaffoldProject` <sub>L1</sub>

[L1][Project] One-shot project generator: from a single JSON spec it creates the project, adds PLC (and optional Unified HMI) hardware, builds UDTs/global DBs/PLC tag tables, imports SCL external sources and LAD S7DCL documents, compiles, sets up the HMI connection/screens/tags, and saves — collapsing the ~20-step runbook into one call. Auto-connects if needed. Critical-step failures (connect/createProject/PLC device) abort; per-element failures are collected and reported. Spec keys: projectName(required); directoryPath?(default %TEMP%); plcName?(PLC_1); plcFamily?(S7-1500); plcMlfb?; hmiName?(omit to skip all HMI); hmiFamily?(WinCCUnifiedPC); hmiSoftwarePath?(HMI_RT_1); connectionName?(HMI_Connection_1); udt?/globalDb?/tagTable? = arrays of the same json objects PlcBuildAndImport accepts; sclSourceFiles? = array of .scl file paths; ladDocs? = array of {importPath,name}; hmiScreens? = array of {screenName,width,height,designJson(object)}; hmiTags? = array of {tagTableName?,tagName,hmiDataType?,plcTag?,address?}; compile?(true); save?(true). Returns a per-step report with compile error/warning counts. dryRun DEFAULTS TO TRUE (safety): the default call only validates the spec offline (PLC block JSON shapes, SCL/LAD file paths, designJson) WITHOUT connecting to TIA or creating anything; after a clean dry run, call again with dryRun=false to actually create the project.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `spec` | `string` |  | JSON object describing the project to generate. See tool description for keys. |
| `dryRun` | `bool` | `true` | DEFAULT true — validates the spec offline (no TIA connection, nothing created). Pass dryRun=false explicitly to actually create the project (after a clean dry run). |

返回：`ResponseScaffold`

---

<a id="reflection"></a>
## Reflection（7 个）

### `DescribeObject` <sub>L2</sub>

[L2][Reflection]Describe an Openness object via reflection. Use this first when a natural-language TIA operation has no direct MCP tool. objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `objectKind` | `string` |  | Object kind: Project\|Portal\|Device\|DeviceItem\|Software\|Block\|Type\|HmiScreen\|HmiTag\|HmiScreenItem |
| `objectPath` | `string` |  | Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. |
| `softwarePath` | `string` | `""` | softwarePath required for Block/Type |
| `maxMembers` | `int` | `200` | Max member count to return |

返回：`ResponseObjectDescribe`

### `DescribeObjectProperty` <sub>L2</sub>

[L2][Reflection]Describe an object's nested property via reflection (members list). propertyPath supports dotted path.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `objectKind` | `string` |  | Project\|Portal\|Device\|DeviceItem\|Software\|Block\|Type |
| `objectPath` | `string` |  | object path |
| `propertyPath` | `string` |  | dotted property path, e.g. 'Connections' or 'PressedStateTags' |
| `softwarePath` | `string` | `""` | required for Block/Type |
| `maxMembers` | `int` | `200` | max member count |

返回：`ResponseObjectDescribe`

### `DescribeService` <sub>L2</sub>

[L2][Reflection]GetService bridge: describe a service object (by type name suffix) from a target object.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `objectKind` | `string` |  | Target object kind: Project\|Portal\|Device\|DeviceItem\|Software\|Block\|Type |
| `objectPath` | `string` |  | Target object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. |
| `serviceTypeSuffix` | `string` |  | Service type suffix, e.g. CrossReferenceService or ICompilable |
| `softwarePath` | `string` | `""` | softwarePath required for Block/Type |
| `maxMembers` | `int` | `200` | Max member count to return |

返回：`ResponseObjectDescribe`

### `GetObjectProperty` <sub>L2</sub>

[L2][Reflection]Get an Openness object property by dotted path. Use after DescribeObject/DescribeObjectProperty to safely inspect current state before writing.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `objectKind` | `string` |  | Object kind: Project\|Portal\|Device\|DeviceItem\|Software\|Block\|Type\|HmiScreen\|HmiTag\|HmiScreenItem |
| `objectPath` | `string` |  | Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. |
| `propertyPath` | `string` |  | Property path, e.g. Name or BlockGroup.Groups |
| `softwarePath` | `string` | `""` | softwarePath required for Block/Type |

返回：`ResponseObjectValue`

### `InvokeObject` <sub>L2</sub>

[L2][Reflection]Invoke an Openness method via reflection. Default is read-oriented; set allowWrite=true only after DescribeObject confirms the target method/signature. This is the generic bridge for public API operations not yet wrapped by MCP.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `objectKind` | `string` |  | Object kind: Project\|Portal\|Device\|DeviceItem\|Software\|Block\|Type\|HmiScreen\|HmiTag\|HmiScreenItem |
| `objectPath` | `string` |  | Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. |
| `methodName` | `string` |  | Method name (case-insensitive) |
| `args` | `System.Text.Json.JsonElement[]?` | `null` | JSON array of args, e.g. ["AttrName"]. Empty for no args. |
| `softwarePath` | `string` | `""` | softwarePath required for Block/Type |
| `allowWrite` | `bool` | `false` | Allow write/dangerous methods. Default false. |

返回：`ResponseObjectValue`

### `InvokeService` <sub>L2</sub>

[L2][Reflection]GetService bridge: invoke a method on a service object (by type name suffix) from a target object.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `objectKind` | `string` |  | Target object kind: Project\|Portal\|Device\|DeviceItem\|Software\|Block\|Type |
| `objectPath` | `string` |  | Target object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. |
| `serviceTypeSuffix` | `string` |  | Service type suffix, e.g. CrossReferenceService or ICompilable |
| `methodName` | `string` |  | Method name (case-insensitive) |
| `args` | `System.Text.Json.JsonElement[]?` | `null` | JSON array of args, empty for no args |
| `softwarePath` | `string` | `""` | softwarePath required for Block/Type |
| `allowWrite` | `bool` | `false` | Allow write/dangerous methods. Default false. |

返回：`ResponseObjectValue`

### `ListObjectChildren` <sub>L2</sub>

[L2][Reflection]List child items from an enumerable Openness property, e.g. Devices, DeviceItems, Connections, Screens, Blocks. Use to discover paths instead of guessing.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `objectKind` | `string` |  | Object kind: Project\|Portal\|Device\|DeviceItem\|Software\|Block\|Type\|HmiScreen\|HmiTag\|HmiScreenItem |
| `objectPath` | `string` |  | Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath. |
| `collectionProperty` | `string` |  | Enumerable property name/path, e.g. Devices, DeviceItems, BlockGroup.Blocks |
| `softwarePath` | `string` | `""` | softwarePath required for Block/Type |
| `limit` | `int` | `200` | Max child items to return |

返回：`ResponseObjectChildren`

---

<a id="reports"></a>
## Reports（6 个）

### `BuildReleaseDiagnosticReport` <sub>L2</sub>

[L2][Reports]Build an offline diagnostic report from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `offlineReleaseSuiteJsonPath` | `string` |  | path to offline_release_validation_suite_*.json. |

返回：`ResponseJsonReport`

### `BuildReleaseManifest` <sub>L2</sub>

[L2][Reports]Build an offline machine-readable release manifest from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `offlineReleaseSuiteJsonPath` | `string` |  | path to offline_release_validation_suite_*.json. |

返回：`ResponseJsonReport`

### `BuildReleaseRunbook` <sub>L2</sub>

[L2][Reports]Build an offline first-user runbook from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `offlineReleaseSuiteJsonPath` | `string` |  | path to offline_release_validation_suite_*.json. |

返回：`ResponseJsonReport`

### `GenerateAcceptanceReport` <sub>L0</sub>

[L0][Reports]Generate a read-only acceptance report for the current MCP/TIA environment. The default mode does not attach to TIA or write to the project; it writes Markdown/JSON report files to outputDirectory.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `outputDirectory` | `string` | `""` | Directory where the Markdown and JSON reports will be written. Empty means a temp directory under %TEMP%. |
| `connectIfNeeded` | `bool` | `false` | When true, call Connect during self-test if the server is not connected. This may attach to TIA Portal. |
| `includeProjectTree` | `bool` | `false` | When true, include project tree output if a project is open. |
| `inspectPortalProcesses` | `bool` | `false` | When true, enumerate TIA process/project details. This may be slow if TIA is contended. |
| `title` | `string` | `"TIA MCP Acceptance Report"` | Optional report title. |

返回：`async Task<ResponseAcceptanceReport>`

### `GenerateErrorReport` <sub>L0</sub>

[L0][Reports]Generate a standardized Markdown/JSON error report. This is file/report generation only; it does not touch TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `errorCode` | `string` |  | Machine-readable error code, for example CompileError, HmiBindingError, TiaSessionContention, UnexpectedOpennessError. |
| `summary` | `string` |  | Short human-readable summary. |
| `detail` | `string` | `""` | Detailed error text, stack trace, compile message, or diagnostic context. |
| `recommendedNextActions` | `string` | `""` | Comma-separated next actions. |
| `severity` | `string` | `"error"` | info, warn, error, critical. |
| `outputDirectory` | `string` | `""` | Directory where the Markdown and JSON reports will be written. Empty means a temp directory under %TEMP%. |

返回：`ResponseErrorReport`

### `RebuildReleaseHandoffArtifacts` <sub>L2</sub>

[L2][Reports]Rebuild diagnostics, runbook, and manifest files from an existing OfflineReleaseValidationSuite JSON report. Offline-only and does not connect to TIA Portal.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `offlineReleaseSuiteJsonPath` | `string` |  | path to offline_release_validation_suite_*.json. |
| `outputDirectory` | `string` |  | directory where rebuilt handoff artifacts will be written. |

返回：`ResponseJsonReport`

---

<a id="validation"></a>
## Validation（3 个）

### `RunHmiTemplatePlcSyncPrecheckSuite` <sub>L2</sub>

[L2][Validation]Offline-only helper: verify Unified HMI template RequiredTags against real PLC tag/DB-member XML symbols before any HMI binding. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `templateDirectory` | `string` |  | directory containing Unified HMI template JSON files. |
| `plcXmlPath` | `string` |  | PLC XML file or directory exported from TIA, containing tag tables and/or GlobalDB XML. |
| `reportDirectory` | `string` |  | directory where reports will be written. |
| `mappingFilePath` | `string` | `""` | optional explicit mapping file produced by the mapping skeleton flow. |

返回：`ResponseJsonReport`

### `RunOfflineReleaseValidationSuite` <sub>L2</sub>

[L2][Validation]Offline-only helper: run the release smoke suite covering PLC Builder, Classic HMI, PLC symbol extraction, Unified HMI template layout, HMI action recipes, and online-monitoring safety guardrails. It does not connect to TIA Portal or modify projects.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `workspaceRoot` | `string` |  | repository/workspace root containing TMP_EXPORT, docs, and tools. |
| `reportDirectory` | `string` |  | directory where suite files and reports will be written. |

返回：`ResponseJsonReport`

### `RunV2PlanCompletionAudit` <sub>L2</sub>

[L2][Validation]Offline-only strict audit for docs/TIA_MCP_常见操作全覆盖方案_V2_二次优化计划.md. It reports verified hard-gate percentage and blocks 100% claims when real TIA/online evidence is missing.

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `workspaceRoot` | `string` |  | repository/workspace root containing docs, tools, and reports. |
| `reportDirectory` | `string` |  | directory where V2 audit reports will be written. |

返回：`ResponseJsonReport`

---
