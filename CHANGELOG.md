# Change Log

## [0.0.36] - 2026-05-31

### 削减工具表面 — 移除 4 个纯别名工具（破坏式，仅影响直呼旧名的脚本）

- 移除 `ExportBlockAsScl` / `ExportBlocksAsScl` / `ImportBlockFromScl` / `ImportBlocksFromScl` 这 4 个工具。它们只是 `ExportAsDocuments` / `ExportBlocksAsDocuments` / `ImportFromDocuments` / `ImportBlocksFromDocuments` 的薄别名（一行 `=>` 转发），功能 **100% 由后者覆盖**。
- 把别名里携带的「PREFERRED on V21+ / 比 SimaticML XML 更易读、diff 友好」引导文案**迁移到对应的 `*Documents` 工具描述**，并补上 `.s7dcl/SCL` 关键词，避免 AI 选型时丢失指引。
- 同步更新 `skill/SKILL.md`（LAD/文本块导入改指 `ImportBlocksFromDocuments`/`ExportBlocksAsDocuments`）、两个 LAD-XML 工具描述、README（中/英）。
- **目的**：消除模型在 Export/Import 簇里的重复选项（同一操作两个名字），降低工具表面膨胀。`*Documents` 一族保留，旧别名名不再注册。
- 重建 V20/V21 exe（0.0.36）。

## [0.0.35] - 2026-05-31

### 内部清理（无用户可见行为变更）

- 删除死代码 `ModelContextProtocol/LadNetworkBuilder.cs`（触点/线圈/并联 LAD 构建器，从未接成任何 MCP 工具；通用梯形图已改走 S7DCL，见 0.0.34）。
- 修复全部编译告警 → **0 警告 0 错误**：`PlcBuilderToolJson.cs`（`lastTokenText` 改 `string?`、移除未用变量 `tightAfter`）；`Portal.cs`/`McpServer.cs`/`Helper.cs` 的可空性误报用 `!`/`??=` 收口（`IsNullOrWhiteSpace` 守卫后无法收窄等，行为不变）。
- 重建 V20/V21 exe（0.0.35，0 错误 0 警告）。

## [0.0.34] - 2026-05-31

### 修复中文乱码 + 梯形图生成引导到 S7DCL

- **中文乱码修复**：`Portal.cs` 的 `NormalizeEngineeringVersion` 改名为 `PrepareXmlForImport`，块/类型 XML 导入时在临时副本上**无条件强制 UTF-8 BOM**（不再仅"保留"原编码）+ 修正 `<Engineering version>`。此前模型/模板写出的无 BOM XML 一导入就把中文注释/块名变成乱码；现已兜底（用户原文件不改）。接入 `ImportBlock`/`ImportType`/批量导入三处。
- **梯形图（LAD）生成引导**：SKILL §9 重写为「优先 S7DCL 文本、FlgNet XML 降为 fallback」。根因——能生成 LAD 的工具（`BuildFlgNetCallXml`/`ComposePlcLadFcBlockXml`）只支持"FC 调用网络"，普通触点/线圈梯形图无 XML 工具，手写 FlgNet 易报错。新增可从零编写的 `.s7dcl` LAD 语法 + 真机样例（`skill/lad-cookbook/MCPVerify_FC_LAD.s7dcl/.s7res` 等），用 `ImportBlocksFromScl` 导入；两个 LAD-XML 工具描述加 NARROW SCOPE 提示；§15 加硬规则 6。
- **示例项目质量**：SKILL §7 加「构建完整示例项目」清单 + 标准 HMI 变量连接/驱动规范；§12 加完整 1024×768 仪表盘 `designJson` 范本（仅用已验证 schema 键）。
- 重建 V20/V21 exe（0.0.34，0 错误）。

## [0.0.33] - 2026-05-28

### 去除内部"商业"措辞（发布质检工具）

- 发布质检的 `CommercialReadinessGateBuilder` → `ReleaseReadinessGateBuilder`（文件同步改名）；`Commercial(ization) Readiness Gate` → `Release Readiness Gate`；JSON 键 `commercialReadinessGate`/`commercialReady`/`commercialReadinessReason` → `release*`。涉及 `OfflineReleaseValidationSuite`/`ReleaseHandoffArtifactBuilder`/`ReleaseManifestBuilder`/`Program.cs`，读写成对改名，数据流与行为不变。
- README 删除已过时的"商业锁"说明（自 0.0.32 起已无任何授权代码）。
- 保留少量工具描述里的 "commercial"（指生产/商用用途，非授权语义）。
- 重建 V20/V21 exe（0.0.33，0 错误）。

## [0.0.32] - 2026-05-28

### 移除商业授权脚手架（全开源）

- 删除 `CommercialLicense.cs`（机器码、RSA license 校验、`commercial.lock` 启动拦截）及 `Program.cs` 中的三处调用。
- 删除 `CliOptions` 的 `--license-machine-code` / `--license-check` 两个 CLI 标志及其属性。
- 仓库本就是 MIT、无 `commercial.lock`（公开版一直免 license 运行）；本次彻底移除商业授权代码，仓库纯开源、无歧义。
- 重建 V20/V21 exe（0 错误，`serverVersion=0.0.32`）。
- 注：`CommercialReadinessGateBuilder`（发布质检报告生成器，非授权）保留不动。

## [0.0.31] - 2026-05-28

### 版本能力层（Capability layer）

- 新增 `Siemens/Capability.cs`：把"某功能在当前连接的 TIA 版本上是否可用"收口为单一真源。`TiaFeature` 枚举（`HardwareHmiConnection` 需 V21+、`DocumentExport` 需 V20+）+ `IsSupported`/`RequireSupported`/`Describe`/`Snapshot`。
- 新增错误码 `PortalErrorCode.NotSupportedOnVersion`；`Portal.cs` 中 `ExportAsDocuments` 的手写 `<20` 守卫改走 `Capability.RequireSupported(DocumentExport)`；`ProbeCreateHardwareHmiConnection` 的 V20 降级提示改走 `Capability.Describe`（统一文案来源）。
- `Bootstrap` 响应新增 `Capabilities` 字段：AI 模型一上来就能看到当前版本能干什么，无需靠失败调用试探。**已在 V20/V21 两份 exe 上实测**：V20 上 `HardwareHmiConnection.supported=false`、`DocumentExport.supported=true`；V21 上两者皆 true。

### "Did you mean" 候选提示整合

- 把原先内联在 `ExportBlock` 里的块名候选提示抽成可复用助手 `BuildBlockDidYouMean`，并复活此前为死代码的 `Guard.DidYouMean`。
- 新增 `BuildTypeDidYouMean` 并应用到 `ExportType` 的 NotFound（此前只返回 "Type not found." 无候选）。

### HTTP transport 修复（此前 POST 完全不可用）

- **根因**：请求体读取与 HTTP↔MCP 内部管道的写入走 APM 包装的异步 I/O，在 .NET Framework `HttpListener` 输入流上会无限挂起，导致每个 `POST /mcp` 永久阻塞（此前只有 `GET /mcp/health` 可用）。
- **修复**：请求体读取、管道写入改为同步；响应读取改为 `Task.Run` 内同步 `ReadLine` 并与 30s 超时竞速（超时返回 504，不再无限挂起）。
- **已用 curl 端到端实测**：`initialize`→200+会话、`notifications/initialized`→202、`tools/call Bootstrap`→200 且返回 Capabilities。

### 构建

- V20 + V21 两份 exe 重建，0 错误，`serverVersion=0.0.31`。

## [0.0.30] - 2026-05-28

### 修复：V20 导入报「engineering version 'V21' is not supported」

- **故障现象**：在 TIA Portal V20 上调用 `PlcBuildAndImport` / `ImportBlock` / `ImportType` 时，导入失败并报错 `The engineering version 'V21' in line 3, position 16 is not supported.`，DB/FC/FB/UDT 全部无法导入。
- **根因**：`Program.cs` 中 21 处 XML 生成器把块头 `<Engineering version="V21"/>` 写死。0.0.28 的双 binary 只解决了 DLL/IL 程序集绑定，并未修正 XML 里的版本号；V20 用户即便跑 V20 exe、能连上、能 dryRun，一旦真导入仍因版本号高于所连博途而被拒。
- **修复**：在导入边界集中归一化，而非逐个改 21 处字面量。`Siemens/Portal.cs` 新增 `NormalizeEngineeringVersion(path)`：导入前把文件中的 `<Engineering version="V\d+"/>` 改写为运行时检测到的 `Engineering.TiaMajorVersion`，写入临时副本（**不修改用户原文件、保留 BOM**），再交给 Openness 导入。已接入 `ImportBlock`、`ImportType`、批量导入循环三处；`.s7dcl` 的 `ImportFromDocuments` 路径不含该字段，无需改动。
- **影响**：V20/V21 两版客户端无需改调用方式，导入自动匹配所连博途版本。改完需重新编译 `TiaMcpServer.exe` 方可生效。

## [0.0.29] - 2026-05-26

### 完整交付包（含运行时）+ GitHub Release

- Git 跟踪 `tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48/`（V21）与 `bin-v20/Release/net48/`（V20）已编译 `TiaMcpServer.exe` 及依赖 DLL；`.gitignore` 仅排除 `bin/Debug`、`bin-v20/Debug` 与 `obj`，不再排除 Release 产物。
- [GitHub Releases / v0.0.29](https://github.com/bulaofen0036-coder/TIA_MCP_260514/releases/tag/v0.0.29) 提供 **`TIA_MCP_完整交付包_v0.0.29.zip`**：与仓库根目录内容一致（含双版本 exe），打包时排除 `.git` 与 `TiaMcp_Output/`。
- `manifest/package-manifest.json`：`bundleVersion` **0.0.29**，`refreshedAt` / `validationSnapshot.performedAt` 对齐本次推送。
- 增强编译错误回传：递归展开 `CompilerResult.Messages`，返回叶子级诊断（含 `Path`/`Description`，并统计 `errorDetailCount`/`warningDetailCount`）。

## [0.0.28] - 2026-05-26

### V20 + V21 双版本支持

- **现实**：V21 把 `Siemens.Engineering.dll` 拆成 `Siemens.Engineering.Base/Step7/WinCC/...` 多个 DLL，V20 仍是单体 `Siemens.Engineering.dll`。同一份 exe 不能同时支持两者（IL 硬绑定不同 assembly identity）。结论：**两份 exe** 分别编译。
- 新增 `TiaMcpServer.V20.csproj`：引用 `Siemens.Collaboration.Net.TiaPortal.Packages.Openness 20.0.1744190253`，定义 `TIA_V20` 编译符号，输出到 `bin-v20/`。
- `Siemens/Portal.cs`：用 `#if TIA_V20` 把 `Siemens.Engineering.HW.CommunicationConnections.*`（V21-only）改成 `Type.GetType()` 反射查找，找不到时硬件级 HMI 连接功能降级为 no-op（其他工具不受影响）。
- 新 CLI 参数 `--tia-portal-location <path>`（两份 exe 都支持）：显式指定 TIA Portal 安装根目录，解决博途装在非默认位置（如 `D:\app\TIA20\Portal V20`）时注册表/`TiaPortalLocation` 环境变量缺失的问题。
- `Engineering.GetTiaPortalInstallPath`：优先级调整为 **CLI override → `TiaPortalLocation` env → 注册表 `HKLM\...\TIAP{N}\TIA_Opns\Path`**。
- `Engineering.DetectTiaMajorVersion`：把 CLI override 加入候选源。

### S7DCL/SCL 文本格式专用 MCP 工具

- 新增 4 个工具：`ExportBlockAsScl`, `ExportBlocksAsScl`, `ImportBlockFromScl`, `ImportBlocksFromScl`，是 `ExportAsDocuments`/`ExportBlocksAsDocuments`/`ImportFromDocuments`/`ImportBlocksFromDocuments` 的薄别名。Description 强调「PREFERRED on V21+」「SIMATIC SD textual format (.s7dcl + .s7res)」，让 AI 更容易首选文本格式。
- 原 `*Documents` 工具保持原样，向后兼容。

### 端到端验证

- V21：DemoProjects/MCP_Demo_Rich_20260523，ExportBlocksAsScl 导出 8 块（含 LAD/SCL/DB），ImportBlocksFromScl 全部 8 块回环成功（14.7s）。
- V20：江夏测试5T车_V20，CompileSoftware → ExportBlocksAsScl，**51 个 .s7dcl + 33 个 .s7res 全量导出成功**。LAD 块格式正确（`RUNG / I_Contact / Coil / TON{...}`）。

### GitHub 交付包同步

- 公开仓库 [bulaofen0036-coder/TIA_MCP_260514](https://github.com/bulaofen0036-coder/TIA_MCP_260514) 从 `TIA_MCP_交付包_20260512_151308` 全量刷新至 `TIA_MCP_交付包_20260525_V20S7DCL_184330`。
- 首次推送以源码为主；**V21/V20 双 exe 运行时**自 **v0.0.29** 起纳入仓库并随 Release zip 分发。

## [0.0.27] - 2026-05-09

### Audit Pass — Stability, Tool Surface, Online Operations

**Online operations (T1) — gap analysis + targeted implementation**

- Static API feasibility report against `D:\app\TIA21\Portal V21\PublicAPI\V21\net48\*.xml`. Confirmed: CPU RUN/STOP control, fault buffer read, ClearForces, and selective per-block download are **not** exposed by Openness PublicAPI. Captured in new `docs/openness-limitations.md` so AI agents stop attempting unreachable operations.
- New: `CompareSoftwareToOnline(softwarePath, maxDepth, maxEntries)` — wraps `PlcSoftware.CompareToOnline()` and walks the resulting `CompareResult` tree via reflection. Returns `ResponseCompare { IsOnline, Entries[], Summary, Truncated }` where each entry has `{ Path, LeftName, RightName, Status, Details }`. Validated live against a 1212C: 26 entries returned, real `PLC tags ObjectsDifferent` correctly surfaced.
- New: `password` parameter on `GoOnline` and `DownloadToPlc`. Hooks `ConnectionConfiguration.OnlineLegitimation` with a `SecureString`-backed handler responding to `OnlinePasswordConfiguration` prompts. `IDisposable`-scoped to guarantee handler unsubscription.

**Bug fix: OnlineProvider/DownloadProvider resolution on nested 1200/1500 CPUs**

- 1200/1500 CPUs in nested device groups expose Online/Download providers on the CPU `DeviceItem`, not on `PlcSoftware`. Previous code only queried `plcSoftware.GetService<T>()` and reported "service not available" / "Offline" even when the PLC was online via TIA Portal UI.
- New helper `Portal.ResolvePlcService<T>(softwarePath, plcSoftware)` walks `SoftwareContainer.Parent` DeviceItem chain when the direct lookup fails. Applied to all 6 call sites: `GetOnlineState`, `GoOnline`, `GoOffline`, `DownloadToPlc`, `CheckDownloadReadiness`, `CompareSoftwareToOnline`.
- Verified: `GetOnlineState` now correctly reports `Online` against the live PLC where it previously misreported `Offline`.

**Error handling — silent failures eliminated on critical paths**

- `Portal.cs`: 6 silent `catch (Exception)` sites now log instead of swallowing — `Dispose()` ×2, `CreateProject`, `OpenSession`, `GetBlocks`, `GetUserDefinedTypes`. Inner-loop catch in `ImportBlocksFromDocuments` logs per-file failures rather than silently skipping.
- Reflection-heavy probe-then-skip patterns (regex validation, parent traversal, multi-SDK-version probes) intentionally left silent — adding logs there is noise without signal.

**Tool surface — `[Category]` 100% coverage + vocabulary normalization**

- 53 → 180 tools tagged with canonical `[Category]` prefix (100% coverage).
- 9 inconsistent prefixes normalized: `Hardware/Network` → `Hardware`, `Plc/Build` → `PLC-Builders`, `HmiUnified/Theme|Layout` → `HMI-Unified`, `HmiUnified/GlobalLibrary[Template]` → `HMI-Library`, `Online/ReadOnly` → `Online-Monitoring`, `PLC-Build+Import` and `PLC-Tags` → `PLC-Software`.
- Two coexisting tag formats: simple `[Category]` (~85 tools) and elaborate `[Category:NAME][flags][PreCondition:...]` (~20 tools, primarily `PLC-Online` / `PLC-Alarms` / `PLC-OpcUA` / `PLC-TechnologyObjects`). Elaborate format is the target convention; full migration deferred.

**Typed Response surface (M3, partial)**

- `ResponseJsonReport` enriched with optional well-known fields: `Errors[]`, `Warnings[]`, `OutputPath`, `OutputFiles[]`. AI clients now have a stable contract for the most common builder/validator outputs across ~36 tools that still use the catch-all type.
- `GetTechnologyObjects` migrated off `ResponseJsonReport` to dedicated `ResponseTechnologyObjectList { Ok, SoftwarePath, Count, Items[] }` with `TechnologyObjectInfo { Name, OfSystemLibElement, OfSystemLibVersion, TypeHint }`. Reference pattern for future migrations.
- New `ResponseCompare` + `CompareEntry` types for `CompareSoftwareToOnline`.

**Test infrastructure**

- New `tests/TiaMcpServer.Test/TestCompareToOnlineLive.cs` — live validation against running TIA Portal session.
- `AssemblyHooks.cs`: `[AssemblyInitialize]` now installs Openness resolver AND a manual `AppDomain.AssemblyResolve` fallback for `Siemens.Engineering*` assemblies (probes `TiaPortalLocation` env var). Required because the package-provided resolver doesn't always hook in time under MSTest's test host.
- `App.config`: removed broken `privatePath` probing pointing to a hardcoded V20 path that was never reachable (privatePath only honors AppBase-relative paths).

**Documentation**

- New `docs/openness-limitations.md` enumerates which TIA Openness capabilities are documented vs require OPC UA / are unreachable. Useful for AI agents to redirect users when a request maps to an out-of-scope capability.
- README aligned with current state: tool count 175+ → 180; new Online operations bullet covers Compare and password support; V21 default; link to openness-limitations.

**Repo hygiene**

- Root `.gitignore` covers `dist/`, IDE noise (`.idea/`, `*.user`, `*.suo`), NuGet (`packages/`, `*.nupkg`), OS files (`Thumbs.db`, `.DS_Store`). `bin/`/`obj/` continue to be handled by per-project `.gitignore`.

## [0.0.26] - 2026-05-09

### T2-E: Technology Objects (3 new tools)
- New: `GetTechnologyObjects` — list all TOs with name, type (OfSystemLibElement), firmware version
- New: `ExportTechnologyObject` — export single TO to XML (follows same pattern as ExportBlock)
- New: `ExportTechnologyObjectsToDirectory` — batch export with regex filter
- Portal.cs: `ResolveTechnologyObjectCollection` helper + `GetTechnologyObjects`, `ExportTechnologyObject`, `ExportTechnologyObjectsToDirectory`
- T2-C skipped: Safety program compilation not accessible via public Openness API (AddIn framework only)

### T3-D: Nullable Warning Elimination
- Build now produces **0 warnings, 0 errors** (previously 32 warnings)
- Fixes applied across Portal.cs, McpServer.cs, Program.cs:
  - CS8602: Added `!` null-forgiving after `IsNullOrWhiteSpace`/`IsNullOrEmpty` guards (14 sites)
  - CS8604: Added `!` / `?? ""` at null-argument call sites (8 sites)
  - CS8619: `Array.ConvertAll(args!, a => a!)` for `object?[]` → `object[]` (3 sites)
  - CS8620: `ReferenceEqualityComparer.Instance!` for IEqualityComparer nullability (2 sites)
  - CS8601: `ipAddress!` in reflection Invoke call (1 site)
  - Program.cs: `LogDiag(x.Message ?? "...")` for nullable Message properties (4 sites)

## [0.0.24] - 2026-05-08

### T2-B: OPC UA Server Configuration (4 new tools)
- New: `GetOpcUaConfig` — inventory of all OPC UA server interfaces, SIMATIC interfaces, reference namespaces with Enabled state
- New: `SetOpcUaInterfaceEnabled` — enable/disable any interface type; takes effect after DownloadToPlc
- New: `ExportOpcUaInterface` — export ServerInterface/SimaticInterface/ReferenceNamespace to XML
- New: `ImportOpcUaInterface` — create or update an interface from XML file
- Portal.cs: `#region opcua` with `GetOpcUaConfig`, `SetOpcUaInterfaceEnabled`, `ExportOpcUaInterface`, `ImportOpcUaInterface`; uses `OpcUaProvider` via GetService + reflection chain through CommunicationGroup → ServerInterfaceGroup

## [0.0.23] - 2026-05-08

### T2-A: Alarm Text Management (5 new tools)
- New: `ExportAlarmClasses` / `ImportAlarmClasses` — alarm class definitions export/import
- New: `ExportAlarmTextLists` / `ImportAlarmTextLists` — all text lists as XLSX (multi-language)
- New: `ExportAlarmInstanceTexts` — instance-level alarm texts as XLSX with configurable columns
- Portal.cs: `#region alarms` with 5 methods; uses AlarmClassDataProvider/PlcAlarmTextProvider via GetService + PlcAlarmTextlistGroup via reflection

### T3-C: TIA Version Auto-Detection
- Engineering.cs: `DetectTiaMajorVersion()` — scans env var, registry (TIAP* keys), and filesystem (Portal V* dirs); returns highest installed version
- Program.cs: use auto-detected version when `--tia-major-version` not specified; logs source of version; falls back to 21 with warning

## [0.0.22] - 2026-05-08

### T3-A: Operation.Run — Centralized Exception Handling
- New: `src/TiaMcpServer/Siemens/Operation.cs` — `Operation.Run(logger, name, action)` / `Run<T>(...)` / `RunValue<T>(...)` with PortalException-aware logging
- Applied to `DisconnectPortal()` as the canonical example
- Full rollout across 60+ Portal.cs methods tracked in TODO.md (T3-A)

## [0.0.21] - 2026-05-08

### T1-B: Watch/Force Table Variable Configuration
- New: `GetPlcForceTables` MCP tool — list force tables (previously only watch tables were exposed)
- New: `SetWatchTableModifyValue` MCP tool — configure a watch table entry (address + value + trigger); write applied when online
- New: `SetForceTableEntry` MCP tool — configure a force table entry (address + forced value); force applied continuously while online
- Portal.cs: `GetPlcForceTables()`, `EnsureWatchTableEntry()`, `EnsureForceTableEntry()` + helpers
  - `FindOrCreateWatchTable`, `FindOrCreateForceTable`, `FindOrCreateTableEntry`, `TryInvokeMethodByName`, `SetEnumPropertyByName`
- API note: Watch/Force Table in TIA Portal Openness is declarative config — actual write/force occurs when TIA Portal is online

## [0.0.20] - 2026-05-08

### T1-A: Download to CPU
- New: `DownloadToPlc` MCP tool — downloads compiled PLC program to physical CPU via `DownloadProvider`
- New: `CheckDownloadReadiness` MCP tool — pre-flight check (DownloadProvider available, network config present) without actual download
- New: `ResponseDownload`, `ResponseCheckDownload` response types
- Portal.cs: `DownloadToPlc()`, `CheckDownloadReadiness()` with auto-accepting download configuration delegates (StopModules, StartModules, DataBlockReinitialization, ConsistentBlocksDownload, CheckBeforeDownload, etc.)
- Reflection-based `Download()` invocation to bypass compile-time ConnectionConfiguration→IConfiguration type mismatch

### T1-C: CPU Online State
- New: `GetOnlineState` MCP tool — reads OnlineProvider.State (Offline/Online/Incompatible/NotReachable/Protected)
- New: `GoOnline` MCP tool — establishes online connection, optional custom IP address
- New: `GoOffline` MCP tool — disconnects online session
- New: `ResponseOnlineState` response type
- Note: CPU operating mode (RUN/STOP) is NOT exposed in TIA Portal public API; documented in tool description

## [0.0.19] - 2026-05-08

- New: HTTP transport (`--transport http --http-prefix http://127.0.0.1:8765/ --http-api-key <secret>`)
- Fix: CliOptions `Logging` comment updated to reflect numeric modes (1=stderr, 2=Debug, 3=EventLog)
- Docs: CHANGELOG typo "Narketplace" → "Marketplace"

## [0.0.16] - 2025-09-02

- New: ImportFromDocuments and ImportBlocksFromDocuments (V20+)
- Guard: Version checks for export/import as documents (V20+)
- UX: Pre-check .s7res for missing en-US tags; warnings surfaced in responses
- Docs: README updates, prompts note V20+ and known LAD en-US limitation
- Refactor: Updated all McpException throws to SDK signature with McpErrorCode
- Chore: Added TODOs for tests/docs

## [0.0.15] - 2025-08-30

- prompts improved
- long running tasks as async tasks

## [0.0.14] - 2025-08-18

- better structure/tree format
- new GetSoftwareTree()
- bugfixes

## [0.0.13] - 2025-08-14

- logging integrated
- prompts added

## [0.0.12] - 2025-08-07

- export path fixed

## [0.0.11] - 2025-08-07

- project structure formatted as markdown code

## [0.0.10] - 2025-08-07

- tool responses improved

## [0.0.9] - 2025-08-04

- export of blocks and types with 'preservePath' option
- new tools
- some infos with attributes

## [0.0.8] - 2025-08-01

- improved jsonrpc responses
- updated dependencies

## [0.0.7] - 2025-07-18

- new GetState()
- return values fixed

## [0.0.6] - 2025-07-16

- refactored code to use new TIA Portal API
- only blocks (OB/FB/FC/DB) and types (UDT) are now retrieved from the PLC software
- use regex to filter blocks and types
- import of blocks and types to PLC software

## [0.0.5] - 2025-07-11

- locating of plc software by softwarePath. This makes it possible to access plc software in groups/subgroups
- new tool: retrieving of project structure as text
- new tool: compile plc software

## [0.0.4] - 2025-06-30

- opens local session or projects, depending on project file extension

## [0.0.3] - 2025-06-23

- Release on Visual Studio Code Marketplace

