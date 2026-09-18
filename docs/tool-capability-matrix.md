# MCP 工具能力矩阵

本文件由源码中的 `[McpServerTool]` 静态抽取生成，运行时仍以 `tools/list` 为准。

- 生成时间：2026-09-18 08:40:00
- 工具数量：224

## L0

### Bootstrap

| Tool | Description |
|---|---|
| Bootstrap | [L0][Bootstrap] FIRST tool any AI model should call. Read-only single-call orientation: returns TIA version, Openness group status, current connection/project state, the recommended next tool, the L0/L1 tool roster, and known TIA Openness limitations. Does NOT connect to TIA Portal — call Connect afterwards based on RecommendedNextTool. |

### Diagnostics

| Tool | Description |
|---|---|
| RunCapabilitySelfTest | [L0][Diagnostics]Run a read-only MCP/TIA readiness self-test. It checks Openness group membership, connection state, visible portal processes, optional automation context, and optional project tree readback without writing to the project. |
| RunOnlineMonitoringSafetySelfTest | [L0][Diagnostics]Run a static, read-only safety self-test for online monitoring guardrails. It does not connect to TIA Portal, open projects, modify watch tables, write PLC values, or expose forced-value operations. |
| Doctor | [L0][Diagnostics] One-call environment doctor for non-experts. Checks TIA install, Openness group membership, and connection/project state, and returns a plain-language diagnosis with the exact fix per problem. When fix=true (default) it ENSURES Openness group membership (adds the current user; may prompt a Windows UAC dialog). Read-only apart from that one fix. Call this first when setup is failing or you are unsure the environment is ready. |

### Guide

| Tool | Description |
|---|---|
| GetAuthoringGuide | [L0][Guide] Verified syntax + workflow cheat sheet for authoring TIA content through this server. CALL THIS BEFORE writing any SCL/LAD/DB/HMI content — it prevents the common encoding, syntax and tool-routing mistakes. Topics: workflow, scl, lad, db, hmi, errors. Read-only, does not touch TIA Portal. |

### Meta

| Tool | Description |
|---|---|
| FindTools | [L0][Meta] Search the FULL tool roster (all ~200 tools), including ones not listed in this session. The server ships a ~48-tool 'lite' roster by default so the tool list stays small and every host can load it; everything else is reached through this tool plus CallTool. USE THIS whenever the visible tools do not cover what you need, before concluding the server cannot do something. Search by capability words, not exact names: 'watch table', 'HMI screen', 'download', 'cross reference', 'GSD'. Returns each match's exact name, parameter signature with defaults, and full description; then invoke it with CallTool. |
| CallTool | [L0][Meta] Invoke ANY tool in the full roster by name, including ones not listed in this session. Use FindTools first to get the exact name and parameter signature. Behaves exactly like calling the tool directly: same work, same result, same safety checks. Example: name='ExportPlcWatchTable', argumentsJson='{\:\,\:\}'. |

### Portal

| Tool | Description |
|---|---|
| GetState | [L0][Portal] Get current connection state: IsConnected, open Project name, and open Session name. Use this to check preconditions before other tools — if IsConnected=false, call Connect first; if Project is empty, call OpenProject or CreateProject. |

### Reports

| Tool | Description |
|---|---|
| GenerateAcceptanceReport | [L0][Reports]Generate a read-only acceptance report for the current MCP/TIA environment. The default mode does not attach to TIA or write to the project; it writes Markdown/JSON report files to outputDirectory. |
| GenerateErrorReport | [L0][Reports]Generate a standardized Markdown/JSON error report. This is file/report generation only; it does not touch TIA Portal or modify projects. |

## L1

### Diagnostics

| Tool | Description |
|---|---|
| ValidateAutomationContext | [L1][Diagnostics]Preflight current project for automation: devices, software, expected PLC/HMI paths, and project tree. |

### Exports

| Tool | Description |
|---|---|
| GetExport | [L1][Exports] Read one page of a large response that was parked under an export handle. When a tool's response exceeds the size limit the engine stores the full text and returns only its head plus an 'exportId'; call this with that id to read the rest. Page forward by passing the 'nextOffset' from the previous page until 'eof' is true. Each page is a raw CHARACTER SLICE of the original text — it can cut a line or a JSON value in half. Concatenate every page first, THEN parse; never parse a single page on its own. Handles live for 24 hours and only inside the current engine session. |
| ListExports | [L1][Exports] List the export handles currently held by this engine session — id, the tool that produced each one, its target, age, and total size. Use it when you have lost an exportId, or to check what is still available before paging. |
| SaveExport | [L1][Exports] Write a parked response to a file in one step, instead of paging it through the conversation. Prefer this whenever the user wants the whole thing (a full cross-reference dump, a whole block list, a whole block export): it costs one call and no context. By default the tool's PAYLOAD is written (e.g. the CSV itself), not the JSON envelope around it — so saving a truncated table to 'IO.csv' really gives you a CSV you can open in Excel. Pass raw=true to write the untouched response text instead. Written as UTF-8 with BOM so Chinese opens correctly in Notepad and Excel. |
| DeleteExport | [L1][Exports] Drop one export handle once you are done with it. Optional — handles expire on their own after 24 hours and the oldest are evicted automatically when the store fills up. |
| ClearExports | [L1][Exports] Drop parked responses in bulk. NOTE: handles already expire on their own at 24h, so the default olderThanHours=24 almost always deletes nothing — pass olderThanHours=0 to actually free the store now. |

### Hardware

| Tool | Description |
|---|---|
| ConnectDeviceNodesToProfinetSubnet | [L1][Hardware] PREFERRED for PROFINET network setup. Finds the first IE/PROFINET node under two devices, creates/reuses a subnet on the first, connects the second, and returns readback evidence. Requires: Connect + OpenProject + both devices added. Typical: firstRootPath='PLC_1', secondRootPath='HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'. Verified for S7-1200 + KTP700 Basic PN. |
| GetDevices | [L1][Hardware] List all hardware devices (PLCs, HMI panels, drives) in the project with their attributes. Requires: Connect + OpenProject. Prefer GetProjectTree for a visual overview; use this when you need the exact device Name and attribute values for subsequent operations like AddDevice or SetDeviceItemAttribute. |
| AddDeviceWithFallback | [L1][Hardware] PREFERRED for natural-language device insertion. Adds a Siemens hardware device by probing the installed TIA catalog with fallback versions. Requires: Connect + OpenProject. Example: 'add CPU 1211C AC/DC/RLY' → family='S7-1200'. Families: S7-1200, S7-1500, WinCCUnifiedPC. For third-party/GSD devices use AddGsdDeviceWithProbe. |
| SearchHardwareCatalog | [L1][Hardware]Search the installed TIA Portal hardware catalog for Siemens or third-party catalog entries by keyword, MLFB/order number, device family, or description. Use this before adding hardware when the exact TypeIdentifier is unknown, especially HMI panels such as KTP700 Basic. |
| GetDeviceIpAddress | [L1][Category:Hardware][PreCondition:Connect+OpenProject] Read a device's configured IP address straight from the TIA project (Openness PROFINET node) — NOT by probing the CPU over S7 and NOT by exporting/parsing AML. Returns the primary IE IP plus all network nodes (address, subnet, type). This is the correct, fast way to discover a PLC's IP before GoOnline/ReadPlcLiveValuesS7. |
| GetProjectTopology | [L1][Category:Hardware][PreCondition:Connect+OpenProject] One-shot, read-only project topology from Openness: every device with its network nodes (IP, subnet, node type). Call this early to understand the project's devices and subnets at a glance, instead of probing S7 or parsing AML. |

### HMI

| Tool | Description |
|---|---|
| CompileAndDiagnoseHmi | [L1][HMI] Compile an HMI and return structured errors/warnings, the HMI counterpart of CompileAndDiagnosePlc. Use it after generating screens/tags so you can read the diagnostics and fix them yourself instead of asking the engineer to compile in the TIA UI. WinCC Unified: HmiSoftware is not compilable on its own, so the owning device is compiled (same as the TIA UI does) and hardware diagnostics may appear alongside screen ones. Classic (Comfort/KTP): the HMI software itself is compiled. Requires: Connect + OpenProject. softwarePath from GetProjectTree, e.g. 'HMI_RT_1'. |

### PLC-Online

| Tool | Description |
|---|---|
| GetOnlineState | [L1][Category:PLC-Online][PreCondition:Connect+OpenProject] Read the current online connection state of a PLC (Offline/Connecting/Online/Incompatible/NotReachable/Protected/Disconnecting). Does NOT change state — purely a read operation. Use before GoOnline to check current state, or after DownloadToPlc to verify the CPU is reachable. State=Online means the PC is communicating with the physical CPU. State=Incompatible means online but firmware/config mismatch — download required. State=NotReachable means network or IP configuration issue. NOTE: This reports Openness connection state, NOT the CPU operating mode (RUN/STOP). The TIA Portal public API does not expose CPU operating mode — check the CPU front panel LEDs or HMI for RUN/STOP status. |
| GoOnline | [L1][Category:PLC-Online][ONLINE-CONNECT][PreCondition:Connect+OpenProject] Establish an online connection from TIA Portal to the physical PLC. Required before DownloadToPlc to confirm reachability, or for future online monitoring tools. Returns State=Online on success. If ipAddress is omitted, uses the IP address configured in the project's hardware configuration. If ipAddress is provided, overrides the configured IP for this session (useful for commissioning with a different IP). Common failures: NotReachable (wrong IP / no cable), Protected (CPU requires authentication — supply password), Incompatible (firmware mismatch). |
| GoOffline | [L1][Category:PLC-Online][PreCondition:Connect+OpenProject] Disconnect the online session between TIA Portal and the physical PLC. Safe to call even if not currently online. Always go offline when monitoring or download is complete. |
| GoOfflineAll | [L1][Category:PLC-Online][PreCondition:Connect+OpenProject] Take EVERY PLC in the open project offline in one call and report each PLC's before/after online state. Use this whenever CompileSoftware/Export*/Import* is blocked by 'operation not permitted in online mode': a UI-initiated online session or a second online PLC is NOT released by GoOffline on a single softwarePath. Fully autonomous — never ask the user to toggle online/offline in the TIA UI, and never OCR the toolbar. |
| CheckDownloadReadiness | [L1][Category:PLC-Online][PreCondition:Connect+OpenProject+CompileSoftware] Check whether a PLC is ready to receive a program download WITHOUT actually downloading. Verifies: DownloadProvider service is available, a network/IP configuration exists in the hardware config. Returns Ready=true only when all checks pass. Use this before DownloadToPlc to surface problems early (missing IP, no hardware config, etc.). Meta.downloadRoutes lists every PG/PC interface -> CPU route (best-ranked first, preferred=true when the adapter shares a subnet with the CPU) — check it on a multi-NIC PC (WLAN/VPN/PLCSIM) before downloading. Does NOT compile — run CompileSoftware first to ensure blocks are consistent. |
| DownloadToPlc | [L1][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+CompileSoftware+CheckDownloadReadiness] Download the compiled PLC program to the physical CPU over the network. The CPU will stop briefly during download and restart automatically (controlled by startAfterDownload). SAFETY: Verify no personnel are near the machine before downloading. This changes live PLC behavior. Workflow: Connect → OpenProject → CompileSoftware → CheckDownloadReadiness → DownloadToPlc → GetOnlineState. On success State=Success or Warning. On Error check Errors[] for details. Default options (keepActualValues=true, consistentBlocksOnly=true) are safe for most scenarios. Set keepActualValues=false only when DB initial values must be reset — this is irreversible. On a multi-NIC PC the PG/PC interface is picked automatically (the adapter sharing a subnet with the CPU); Meta.pgPcRoute reports which one was used. Override with pgPcInterface / targetIpAddress when the pick is wrong. |

### PLC-Software

| Tool | Description |
|---|---|
| DescribeBlockLogic | [L1][PLC-Software] Read a block's LOGIC as READABLE TEXT — the fast, accurate way to analyze LADDER (LAD) without hand-parsing FlgNet XML. For each LAD network it reconstructs the power flow as a boolean-ish expression (series = ' · ', parallel = ' + '), shows coils ( )/(S)/(R), MOVE/compare/timer boxes with their operands, and — critically — FLAGS any contact whose operand is a LITERAL CONSTANT with '⟨常量⟩' (a normally-open contact wired to FALSE silently disables its whole rung; this is nearly impossible to spot by eye). SCL/STL networks are rendered inline as code. Use this to understand or review LAD logic before editing. Requires: Connect + OpenProject + the block consistent (compile first if IsConsistent=false; export does not work in online mode — GoOffline first). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree. |
| ImportBlock | [L1][PLC-Software] Import a single SimaticML XML block file into PLC software. Requires: Connect + OpenProject. importPath must be an absolute path to a .xml file. After import it reads back to confirm the block is present (Meta.verified); call CompileAndDiagnosePlc for full consistency. Pick the right tool: SCL/.s7dcl text → ImportFromDocuments; multiple XML files → ImportBlocksFromDirectory; a full exported program (UDTs+tags+blocks) → ImportPlcProgramFromDirectory; JSON-built blocks → PlcBuildAndImport. |
| CompileAndDiagnosePlc | [L1][PLC-Software] PREFERRED compile tool. Compiles PLC and returns structured errors/warnings by recursively walking CompilerResult.Messages (V20/V21 PublicAPI). Leaf diagnostics include Path + Description; optional Line/Column via GetAttribute when exposed. Requires: Connect + OpenProject. |
| GetSoftwareInfo | [L1][PLC-Software] Get PLC software properties (language, version, block counts). Requires: Connect + OpenProject. softwarePath comes from GetProjectTree (e.g. 'PLC_1'). Use GetSoftwareTree for the full block hierarchy. |
| PlcBuildAndImport | [L1][PLC-Software] MAIN tool for creating new PLC blocks from natural language. Build one PLC artifact (UDT/tag table/GlobalDB/FC/FB) from structured JSON, then optionally import and compile. Use dryRun=true first to validate. Workflow: describe block in JSON → dryRun → review → dryRun=false to import. Replaces the multi-step Build*Xml + ImportBlock sequence. |
| ImportPlcTagTable | [L1][PLC-Software]Import one PLC tag table XML file into PLC software (best-effort) |
| WritePlcSclSourceFile | [L1][PLC-Software][Offline] Write SCL source text to a local .scl external-source file (UTF-8 WITH BOM, so Chinese comments are not imported as mojibake/乱码). This tool does NOT connect to TIA Portal and does NOT import anything — it only writes the file to disk and returns the path plus manual-import instructions. Use it as the robust fallback when XML block import is rejected (e.g. a TIA V20 portal rejecting V21 SimaticML tokens: 'Cannot create SW.Blocks.CompileUnit... token not supported'): the user imports the .scl manually in TIA via project tree → 'External source files' → 'Add new external file', then right-clicks the source → 'Generate blocks from source'. The sclContent must be a complete source, e.g. FUNCTION_BLOCK \ ... END_FUNCTION_BLOCK. |
| CompileSoftware | [L1][PLC-Software] Compile all blocks in the PLC software. Requires: Connect + OpenProject. Returns basic success/failure. For structured error/warning details use CompileAndDiagnosePlc instead. Must compile before ExportBlock if any blocks are inconsistent. After adding new blocks via import, always compile to catch type/interface mismatches. |
| GetSoftwareTree | [L1][PLC-Software] Get the full PLC block/type/external-source hierarchy as ASCII tree. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). ALWAYS call before ExportBlock/ImportBlock to get exact group paths (e.g. 'Program blocks/FBs/FB_Motor'). Returns OB/FB/FC/GlobalDB/UDT/ExternalSource blocks with group hierarchy. |
| ImportType | [L1][PLC-Software]Import a type from file into the plc software |

### Portal

| Tool | Description |
|---|---|
| Connect | [L1][Portal] Connect to a running TIA Portal instance or start a new one. MUST be the first tool called in every session. On success, state becomes Connected=true. If TIA Portal is not installed or the user is not in the 'Siemens TIA Openness' Windows group, this will fail — run EnsureOpennessUserGroup first. |
| ConnectIsolated | [L1][Portal] Start a BRAND-NEW headless TIA Portal instance instead of attaching to a running one. It never attaches to, modifies or closes any TIA window or project the user already has open. USE THIS when the user is working in the TIA Portal UI: plain Connect attaches to their instance and OpenProject then (correctly) refuses to touch their project, so the whole server is unusable until they close it. Must be the FIRST connection tool in a fresh MCP process — calling it after another connection leaves an orphaned portal process that later attaches steal. Afterwards use OpenProject / CreateProject as usual, then CloseProject and Disconnect. |
| ListPortalProcessProjects | [L1][Portal]List running TIA Portal processes and the projects/sessions visible in each process. |
| EnsureOpennessUserGroup | [L1][Portal]Ensure current Windows user is in TIA Openness user group (may prompt UI). Returns success=true when membership is OK. |
| Disconnect | [L1][Portal] Disconnect from TIA Portal and release the Openness handle. Call after all project work is done. Any unsaved changes will be lost — call SaveProject first if needed. |

### Project

| Tool | Description |
|---|---|
| GetProjectTree | [L1][Project] Get the full project device/software tree as ASCII art. Requires: Connect + OpenProject. ALWAYS call this first after opening a project to discover the exact softwarePath (e.g. 'PLC_1') and device paths needed by all other PLC/HMI/hardware tools. Returns device names, software nodes, and HMI nodes. |
| GetProject | [L1][Project] List all open local projects and multi-user sessions with their attributes. Requires: Connect. Use this to confirm which project is active, or to find the project name for AttachToOpenProject. |
| OpenProject | [L1][Project] Open a local TIA Portal project (.apXX) or multi-user session (.alsXX) file, where XX is the TIA version number (e.g. .ap21, .als21). Requires: Connect. Closes any currently open project first. After success, call GetProjectTree to explore its structure. |
| AttachToOpenProject | [L1][Project]Attach MCP to an already-open TIA Portal project by name (avoids disposed project handles). |
| CreateProject | [L1][Project] Create a new empty TIA Portal project. Requires: Connect. After creation, call AddDevice to add PLCs/HMIs, then GetProjectTree to verify. The project is automatically opened after creation — no separate OpenProject call needed. |
| ScaffoldProject | [L1][Project] One-shot project generator: from a single JSON spec it creates the project, adds PLC (and optional Unified HMI) hardware, builds UDTs/global DBs/PLC tag tables, imports SCL external sources and LAD S7DCL documents, compiles, sets up the HMI connection/screens/tags, and saves — collapsing the ~20-step runbook into one call. Auto-connects if needed. Critical-step failures (connect/createProject/PLC device) abort; per-element failures are collected and reported. Spec keys: projectName(required); directoryPath?(default %TEMP%); plcName?(PLC_1); plcFamily?(S7-1500); plcMlfb?; hmiName?(omit to skip all HMI); hmiFamily?(WinCCUnifiedPC); hmiSoftwarePath?(HMI_RT_1); connectionName?(HMI_Connection_1); udt?/globalDb?/tagTable? = arrays of the same json objects PlcBuildAndImport accepts; sclSourceFiles? = array of .scl file paths; ladDocs? = array of {importPath,name}; hmiScreens? = array of {screenName,width,height,designJson(object)}; hmiTags? = array of {tagTableName?,tagName,hmiDataType?,plcTag?,address?}; compile?(true); save?(true). Returns a per-step report with compile error/warning counts. dryRun DEFAULTS TO TRUE (safety): the default call only validates the spec offline (PLC block JSON shapes, SCL/LAD file paths, designJson) WITHOUT connecting to TIA or creating anything; after a clean dry run, call again with dryRun=false to actually create the project. |
| SaveProject | [L1][Project] Save the currently open project or session to disk. Requires: Connect + OpenProject. Call after any significant change (device add, block import, HMI edit). Compile first if there are pending changes to ensure consistency. |
| CloseProject | [L1][Project] Close the currently open project or multi-user session. Requires: Connect + OpenProject. Any unsaved changes are lost — call SaveProject first. After closing, the connection remains active but no project is open. |

### VersionControl

| Tool | Description |
|---|---|
| GetVersionControlWorkspaces | [L1][VersionControl] List this project's version control (VCI) workspaces: name, folder on disk, language, and how many objects are mapped. A workspace is the plain-text mirror of the project that Git can actually diff and commit. Read-only. Requires TIA V21+ and an open project. |
| GetVersionControlStatus | [L1][VersionControl] Per-object status of a VCI workspace: which mapped objects differ between the TIA project and the text files on disk. This is the input for a change log — it names exactly what changed before you commit. Read-only, changes nothing. Status values: Equal (in sync), Unequal (project and file differ), WorkspaceFileMissing (never exported), Unknown. |
| SyncVersionControlWorkspace | [L1][VersionControl] Synchronize a VCI workspace. direction='ProjectToWorkspace' writes the TIA project's objects out as text files (do this before `git commit`); 'WorkspaceToProject' reads the text files back INTO the project (do this after `git pull` / to restore a reviewed version). DEFAULTS TO dryRun=true: the default call only reports what WOULD be synchronized. WorkspaceToProject OVERWRITES blocks in the open project — compile and save afterwards, and it requires a Pro license (exporting is free). |

## L2

### Diagnostics

| Tool | Description |
|---|---|
| RunHmiActionScriptRecipeSafetySelfTest | [L2][Diagnostics]Offline-only helper: prove deterministic HMI button action scripts are allowed only for safe set/reset/toggle bit recipes, while high-risk writes and unverified navigation/popup recipes are blocked. |

### Hardware

| Tool | Description |
|---|---|
| GetDeviceItemIoAddresses | [L2][Hardware] READ-ONLY. List the I/O addresses of one device item (signal module, signal board, built-in CPU I/O). Returns each address as ioType + startAddress + length in ENGINE RAW VALUES (startAddress is the byte offset: %I2.0 is startAddress 2). Use this to confirm an address before and after changing it. Device item path looks like 'PLC_1/DI 8x24VDC_1' — get it from GetDeviceItemTree. |
| SetDeviceItemIoAddress | [L2][Hardware][Destructive] Preview or change the I/O START ADDRESS of one device item (e.g. move a DI module to start at %I2.0 by passing startAddress=2). Defaults to dryRun=true. startAddress is the ENGINE RAW byte offset, not '2.0'. It writes hardware configuration, so a wrong value does NOT fail compilation — the program silently reads a different module. Read back with GetDeviceItemIoAddresses, then CompileSoftware and SaveProject. Overlapping address ranges are rejected by TIA and reported back with the reason. |
| GetDeviceInfo | [L2][Hardware]Get info from a device from the current project/session |
| GetDeviceItemInfo | [L2][Hardware]Get info from a device item from the current project/session |
| GetDeviceItemTree | [L2][Hardware]Get a subtree view for a device item (hardware components + sub device items) |
| GetDeviceItemNetworkInfo | [L2][Hardware]Get network-related attributes for a device item (best-effort heuristic filter) |
| ExportDeviceAml | [L2][Hardware] Read-only: export a device's hardware configuration to an AutomationML (CAx) .aml file. The file contains the configured IP address, subnet/mask, PROFINET device name and topology — info GetDeviceItemNetworkInfo omits. devicePath is the station/device path from GetProjectTree (e.g. 'S7-1200 station_3'). exportPath may be a folder (file named <device>.aml) or a full .aml path. Does NOT modify the project or go online. |
| SetDeviceItemAttribute | [L2][Hardware]Set one exact DeviceItem attribute by name with type coercion and detailed error return. Use GetDeviceItemInfo/GetDeviceItemNetworkInfo first. |
| PlanHardwareNetworkConfiguration | [L2][Hardware][Offline] Validate a hardware network operation plan without connecting to TIA Portal or modifying a project. Use this before EnsureSubnet/AttachDeviceNodeToSubnet/SetCpuCommonSettings; rejects guessed paths, unsafe subnet types, invalid IP/mask/gateway, and CPU settings without exactAttributes. |
| EnsureSubnet | [L2][Hardware] Ensure an Industrial Ethernet/PROFINET subnet by anchoring on a real deviceItemPath from GetProjectTree/GetDeviceItemTree. Applies only through TIA Openness, then returns readback evidence (node path, subnet name, interface path). Does not guess paths. |
| AttachDeviceNodeToSubnet | [L2][Hardware] Attach one real device network node to an existing PROFINET subnet and return readback evidence. deviceItemPath must come from GetProjectTree/GetDeviceItemTree, interfaceIndex selects a discovered Industrial Ethernet/PROFINET node, and online/force operations are never used. |
| SetCpuCommonSettings | [L2][Hardware] Set CPU common settings using exactAttributes JSON only after reading exact attribute names from GetDeviceItemInfo/GetDeviceItemNetworkInfo. The tool writes only those exact attributes, rejects missing/non-writable attributes, and returns readback evidence. |
| ProbeHardwareHmiConnectionOwnerCandidates | [L2][Hardware]Enumerate candidate owner objects for hardware HMI connection creation without calling GetService on each high-level object. |
| ProbeHardwareHmiConnectionWhitelistedServices | [L2][Hardware]Read-only scan of whitelisted services on safe hardware HMI connection owner candidates. Does not create connections and skips high-level project/composition objects to avoid hangs. |
| AddDevice | [L2][Hardware] Add one hardware device using an exact MLFB/order number and version from the TIA hardware catalog. Requires: Connect + OpenProject. Use SearchHardwareCatalog first to find the exact MLFB/version. For unknown or approximate device names, use AddDeviceWithFallback or AddHardwareCatalogDeviceWithProbe instead. |
| SearchInstalledGsdDevices | [L2][Hardware]Search installed TIA V21 hardware catalog and local GSDML files for third-party PROFINET/GSD devices such as AFM60A, ATV320, or DL100. Use this before inserting a non-Siemens device. |
| AddGsdDeviceWithProbe | [L2][Hardware]Add a third-party GSD/GSDML hardware device by first searching the installed TIA hardware catalog, ranking candidates, and inserting with the exact catalog TypeIdentifier. Does not fall back to unrelated Siemens devices. |
| AddHardwareCatalogDeviceWithProbe | [L2][Hardware]Add a hardware device by searching the installed TIA hardware catalog, ranking insertable TypeIdentifier candidates, and inserting the best match. Use for Siemens devices/HMI panels when exact TypeIdentifier is unknown, e.g. KTP700 Basic PN. |
| GetDevicePlugLocations | [L2][Hardware] READ-ONLY. List the plug slots of one device item: which slot numbers are FREE (as reported by TIA at runtime) and which are already OCCUPIED (by name / order number / built-in). Call this BEFORE plugging a signal board (SB), signal module (SM) or communication module (CM) so the slot / position number comes from TIA instead of a guess — slot numbers differ per CPU family and are never hardcoded by this server. A signal board plugs into the CPU device item itself, so pass the CPU path (e.g. 'PLC_1'). Get paths from GetDeviceItemTree. |
| PlugDeviceItem | [L2][Hardware][Destructive] Insert a SUBMODULE into an existing device: signal board (SB, e.g. SB 1221 6ES7221-3BD30-0XB0), signal module (SM), or communication module (CM). This is the 'InsertDeviceItem' / 'AddSignalBoard' operation — AddDevice only creates whole stations and cannot plug boards into a CPU. Defaults to dryRun=true, which runs a REAL TIA feasibility check (CanPlugNew) without writing. Pass positionNumber=-1 to let the server pick a free slot reported by TIA; slot numbers are never hardcoded — use GetDevicePlugLocations to see them. After a successful plug the module is read back and verified (IsPlugged / name / slot). This tool does NOT set addresses: to make the inputs start at %I2.0, call SetDeviceItemIoAddress afterwards with startAddress=2, then CompileSoftware and SaveProject. Failures are reported by category: SlotOccupied, SlotNotAvailable, OrderNumberNotFound, NotSupportedByDevice, PlugFailed, VerifyFailed. |
| DumpDeviceAttributes | [L2][Category:Hardware][PreCondition:Connect+OpenProject] Read-only inventory of EVERY Openness attribute exposed on a device's items (CPU, modules, interfaces, ports): name, access mode (read-only vs read/write), current value, value type. Run this ONCE per CPU/firmware to learn what is actually exposed, then drive hardware reads/writes from that ground truth instead of guessing attribute names. Optional nameFilter narrows to attributes whose name contains a substring (e.g. 'protection', 'putget', 'ip'). NOTE: GetAttributeInfos() does not enumerate every gettable attribute on all CPUs, so absence here means 'not enumerated', not a guaranteed 'no interface'. |
| GetPutGetAccess | [L2][Category:Hardware][PreCondition:Connect+OpenProject] Read whether a CPU permits remote PUT/GET access — the precondition for ReadPlcLiveValuesS7 on DB areas. If enabled=false, S7 absolute reads of DBs will fail; enable with SetPutGetAccess (then hardware DownloadToPlc). |
| SetPutGetAccess | [L2][Category:Hardware][CONFIG-WRITE][PreCondition:Connect+OpenProject] Enable or disable remote PUT/GET access on a CPU (the precondition for S7 DB reads). This is a hardware-configuration change — you must run DownloadToPlc afterwards for it to take effect on the live CPU. Returns before/after readback evidence. |

### HMI

| Tool | Description |
|---|---|
| GetHmiProgramInfo | [L2][HMI] Get HMI software type (Classic/Basic/Unified), version, and list of all screen names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'HMI_RT_1'). Use to confirm HMI type before choosing Classic vs Unified tool variants. |
| DescribeHmiSoftware | [L2][HMI]Describe the HMI software object (members/methods) via reflection. Useful to discover Export/Import/Create APIs. |
| DescribeHmiScreen | [L2][HMI]Describe one HMI screen object (members/methods) by name under an HMI software. |
| DescribeHmiTagTable | [L2][HMI]Describe one HMI tag table object (members/methods) by name under an HMI software. |
| DescribeHmiTag | [L2][HMI]Describe one HMI tag object (members/methods) by name under an HMI tag table. |
| DescribeHmiScreenItem | [L2][HMI]Describe one HMI screen item (widget) by name under an HMI screen. |
| GetHmiScreens | [L2][HMI] List all screen names in an HMI (Classic or Unified). Requires: Connect + OpenProject. softwarePath from GetProjectTree. Use before EnsureUnifiedHmiScreen/ExportHmiScreen to confirm which screens exist. |
| GetHmiTagTables | [L2][HMI]List HMI tag table names (Classic/Unified, best-effort) |
| GetHmiTags | [L2][HMI]List HMI tag names (best-effort). If tagTableName empty, returns tags found at root collection if available. |
| GetHmiTagDetails | [L2][HMI] Read the PROPERTIES of HMI tags in one call: datatype, connection, address, PLC binding, acquisition cycle. Read-only. GetHmiTags gives names only; this gives values, for one tag table or (tagTableName empty) every tag table including the ones filed in a tag table group. attributes: comma-separated list to override the default set (Name,DataType,HmiDataType,Connection,Address,PlcTag,PlcName,AcquisitionCycle); an attribute this tag type does not have comes back null instead of failing the call. Requires: Connect + OpenProject. softwarePath from GetProjectTree, e.g. 'HMI_RT_1'. |
| GetHmiScreenItemDetails | [L2][HMI] Read the PROPERTIES of every item on one HMI screen in one call: type (HmiButton/HmiIOField/HmiText/...), position, size, visibility, plus the screen's own size. Read-only. Screens inside a screen group are found. attributes: comma-separated list to override the default set (Name,Left,Top,Width,Height,Visible,Enabled); an attribute this item type does not have comes back null instead of failing the call. Requires: Connect + OpenProject. softwarePath from GetProjectTree, screenName from GetHmiScreens. |
| GetHmiConnections | [L2][HMI]List HMI connection names (Classic/Unified, best-effort) |
| ExportHmiScreen | [L2][HMI]Export one HMI screen to a file (best-effort; requires Openness export support) |
| ExportHmiTagTable | [L2][HMI]Export one HMI tag table to a file (best-effort; requires Openness export support) |
| ExportHmiConnection | [L2][HMI]Export one HMI connection to a file (best-effort; Classic/Unified via reflection) |
| ExportHmiProgram | [L2][HMI]Batch export HMI screens/tagtables into a directory (best-effort) |
| ImportHmiScreen | [L2][HMI]Import one HMI screen XML file into an HMI program (best-effort; Classic/Unified via reflection) |
| ImportHmiTagTable | [L2][HMI]Import one HMI tag table XML file into an HMI program (best-effort; Classic/Unified via reflection) |
| ImportHmiConnection | [L2][HMI]Import one HMI connection XML file into an HMI program (best-effort; Classic/Unified via reflection) |
| ImportHmiScreensFromDirectory | [L2][HMI]Batch import HMI screen .xml files from a directory (best-effort) |
| ImportHmiTagTablesFromDirectory | [L2][HMI]Batch import HMI tag table .xml files from a directory (best-effort) |

### HMI-Classic

| Tool | Description |
|---|---|
| BuildClassicHmiScreenXml | [L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI screen XML document from structured JSON. It does not connect to TIA Portal, import screens, or modify projects. Validate in a temporary Classic HMI project before using on a real project. |
| BuildClassicHmiTagTableXml | [L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI tag table XML document from structured JSON. Supports plain HMI tags and symbolic PLC bindings through Connection + ControllerTag/PlcTag. It does not connect to TIA Portal, import tags, or modify projects. |
| BuildClassicHmiMinimalPackage | [L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package from structured JSON. It returns tag-table XML, screen XML, import order, and readiness checks that screen item tag references are declared in the tag table. It does not connect to TIA Portal, import files, or modify projects. |
| WriteClassicHmiMinimalPackageFiles | [L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package and write tag-table XML, screen XML, and manifest JSON to an output directory. It does not connect to TIA Portal, import files, or modify projects. |
| ValidateClassicHmiMinimalPackageFiles | [L2][HMI-Classic]Offline-only helper: validate an already written Classic/Basic HMI minimal package folder or manifest. It reads manifest/XML, checks parseability and HMI tag references, and does not connect to TIA Portal or modify projects. |
| ValidateClassicHmiMinimalPackagePlcSync | [L2][HMI-Classic]Offline-only helper: validate that Classic/Basic HMI tag-table ControllerTag/PlcTag bindings exist in a caller-provided exact PLC symbol list. It does not connect to TIA Portal or modify projects. |
| RunClassicHmiOfflineValidationSuite | [L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI validation suite covering PLC symbol extraction, HMI package generation, HMI tag references, and PLC-HMI sync positive/negative gates. It writes reports only to the requested report directory. |
| RunClassicHmiTemporaryImportPreflight | [L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI temporary-import preflight. It checks TIA V21 environment, Openness group, package files, PLC-HMI sync, and emits an import/readback plan without connecting to TIA Portal or creating projects. |

### HMI-Library

| Tool | Description |
|---|---|
| ProbeGlobalLibrary | [L2][HMI-Library]Open a TIA global library (.al21) read-only/best-effort and list accessible master copies/types/folders through public/reflection APIs. It does not import library content. |
| ImportMasterCopyFromGlobalLibrary | [L2][HMI-Library] Import one MasterCopy from a TIA global library into a real Unified HMI screen and return ScreenItems readback evidence. This modifies the project, must be tried in a temporary project first, and reports failure unless the imported item is visible after readback. |
| AnalyzeGlobalLibraryPackage | [L2][HMI-Library]Analyze a TIA global library folder offline by file-system structure. It does not connect to TIA Portal, open the library, import content, or modify files. |
| PlanGlobalLibraryTemplateReuse | [L2][HMI-Library] Plan the commercial fallback when direct MasterCopy import is not publicly verifiable: learn reference/global-library template evidence and rebuild screens with native Unified HMI MCP theme/layout/action tools. Offline planning only; it does not import library content or modify projects. |
| AnalyzeHmiTemplateReference | [L2][HMI-Library]Analyze local Unified HMI JSON templates against reference-project/runtime/global-library hints offline. It does not connect to TIA Portal or modify projects. |
| AnalyzeUnifiedHmiTemplateLayout | [L2][HMI-Library]Offline-only QA for Unified HMI JSON templates. Checks theme metadata, screen bounds, duplicate item names, size issues, layout overlap warnings, density, and execution JSON shape. It does not connect to TIA Portal or modify projects. |

### HMI-Unified

| Tool | Description |
|---|---|
| EnsureStartStopUnifiedHmi | [L2][HMI-Unified] SHORTCUT for motor start/stop HMI. Ensures HMI_Connection_1 uses the correct PLC driver (1200/1500 vs 300/400 from CPU TypeIdentifier), 4 HMI tags (StartPB/StopPB/EStop/RunOut) with symbolic PLC binding, and a simple styled Main screen. Requires: Connect + OpenProject + PLC + Unified HMI. Call after EnsureUnifiedHmiScreen if you need a fixed screen size. Idempotent. |
| EnsureUnifiedHmiScreen | [L2][HMI-Unified] Create or verify a WinCC Unified HMI screen exists. Requires: Connect + OpenProject + Unified HMI. Idempotent. After creating a screen, add tags with EnsureUnifiedHmiTag, add controls with EnsureUnifiedHmiScreenItem, or apply a complete layout with ApplyUnifiedHmiScreenDesignJson. |
| EnsureUnifiedHmiTagTable | [L2][HMI-Unified] Create or verify a Unified HMI tag table exists. Requires: Connect + OpenProject + Unified HMI. Idempotent. Create tag tables before adding tags with EnsureUnifiedHmiTag. Default tag table name is '默认变量表'. |
| EnsureUnifiedHmiTag | [L2][HMI-Unified] Create or verify a Unified HMI external tag. For PLC-backed tags pass plcTag and address in the same call; the address must read back in Address/LogicalAddress, e.g. %DB200.DBX0.0. Requires: Connect + OpenProject + EnsureUnifiedHmiConnection + EnsureUnifiedHmiTagTable. |
| EnsureUnifiedHmiConnection | [L2][HMI-Unified] Create or verify the PLC↔HMI communication connection (HMI_Connection_1 by default). Requires: Connect + OpenProject + both PLC and Unified HMI devices. Must exist before PLC-backed HMI tags can exchange data. Call before EnsureUnifiedHmiTag with plcTag binding. UNIFIED PANELS ONLY: on Classic/Comfort/Basic panels (KTP Basic, TP/KTP Comfort) this connection cannot be created via Openness (CommunicationConnections service is not exposed); if the project needs end-to-end HMI automation, use a WinCC Unified panel instead of a classic one. |
| EnsureUnifiedHmiScreenItem | [L2][HMI-Unified] Create or verify a single Unified HMI control (button, lamp, IO field, etc.) on a screen. Requires: Connect + OpenProject + EnsureUnifiedHmiScreen. itemType: Button, Rectangle (lamp/indicator/background — has NO text), Text (static text label = HmiText), IOField (value display/entry), or full CLR type name. For a text caption/label ALWAYS use Text, never Rectangle (a Rectangle has no Text property and renders blank if given text). For a complete screen layout use ApplyUnifiedHmiScreenDesignJson instead. |
| ApplyUnifiedHmiScreenDesignJson | [L2][HMI-Unified] PREFERRED for natural-language HMI design. Apply a complete JSON layout spec to a screen in one call: screen size + multiple controls (Button/Rectangle/IOField/Text) with positions, text, and properties. Use item type \ (HmiText) for static text labels/titles/field captions — a Rectangle has no Text property and renders blank if given text. Requires: Connect + OpenProject + EnsureUnifiedHmiScreen. Better than calling EnsureUnifiedHmiScreenItem multiple times. Use BuildUnifiedHmiLayoutDesignJson to generate the JSON from a grid description. |
| BuildUnifiedHmiThemeDesignJson | [L2][HMI-Unified][Offline] Build ApplyUnifiedHmiScreenDesignJson-compatible JSON from a theme/palette. It does not connect to TIA Portal or modify projects. |
| BuildUnifiedHmiLayoutDesignJson | [L2][HMI-Unified][Offline] Build ApplyUnifiedHmiScreenDesignJson-compatible JSON from a grid layout. It does not connect to TIA Portal or modify projects. |
| ApplyUnifiedHmiTheme | [L2][HMI-Unified] Apply a theme/palette to a real Unified HMI screen through ApplyUnifiedHmiScreenDesignJson. Requires a connected TIA project; verify with DescribeHmiScreenItem/readback before saving. |
| ApplyUnifiedHmiLayout | [L2][HMI-Unified] Apply a grid layout to a real Unified HMI screen through ApplyUnifiedHmiScreenDesignJson. Requires a connected TIA project; verify changed items with DescribeHmiScreenItem/readback before saving. |
| BuildUnifiedHmiTemplateApplyDesignJson | [L2][HMI-Unified]Offline-only helper: convert one Unified HMI template JSON file into the execution JSON accepted by ApplyUnifiedHmiScreenDesignJson, with layout QA attached. It does not connect to TIA Portal or modify projects. |
| BuildUnifiedHmiTemplateApplyDesignManifest | [L2][HMI-Unified]Offline-only helper: build a directory-level manifest for Unified HMI templates. It summarizes layout QA and execution-design readiness for every unified_*.json template without returning full apply payloads. It does not connect to TIA Portal or modify projects. |
| BindUnifiedHmiButtonPressedTag | [L2][HMI-Unified]Bind a Unified HMI button PressedStateTags entry to an HMI tag (momentary press behavior, best-effort). |
| ListUnifiedHmiApiTypes | [L2][HMI-Unified]List loaded WinCC Unified HMI API types/enums by name filter, useful for discovering event and dynamization types. |
| EnsureUnifiedHmiButtonEventHandler | [L2][HMI-Unified]Ensure a Unified HMI button event handler exists and return its API shape. eventType must match HmiButtonEventType. |
| DescribeUnifiedHmiButtonEventScript | [L2][HMI-Unified]Describe a Unified HMI button event handler Script property and its current object members/attributes. |
| SetUnifiedHmiButtonEventScriptCode | [L2][HMI-Unified]Set ScriptCode on a Unified HMI button event ScriptDynamization. SyntaxCheck is OFF by default because on TIA V21 it can crash the Portal process and lose the script (issue #36); pass syntaxCheck=true only when you need that evidence. |
| BuildUnifiedHmiButtonActionScript | [L2][HMI-Unified]Build a safe Unified HMI button action script from a high-level action recipe without connecting to TIA. |
| EnsureUnifiedHmiButtonAction | [L2][HMI-Unified]Generate and apply a deterministic Unified HMI button action. Only set-bit/reset-bit/toggle-bit are applied; high-risk or TODO recipes are rejected. SyntaxCheck is OFF by default (issue #36: it can crash TIA V21). |
| EnsureUnifiedHmiDynamization | [L2][HMI-Unified]Ensure a Unified HMI item property dynamization exists using a concrete dynamization type and return its API shape. |
| BindUnifiedHmiTagDynamization | [L2][HMI-Unified]Ensure a Unified HMI TagDynamization exists for an item property and bind it to an HMI tag. |

### Online-Monitoring

| Tool | Description |
|---|---|
| ProbePlcMonitorOnlineCapabilities | [L2][Online-Monitoring]Read-only probe for PLC online/offline/watch/monitor API surfaces. It does not go online/offline, change watch tables, write values, or touch restricted safety APIs. |
| ReadPlcWatchTableCurrentValuesReadOnly | [L2][Online-Monitoring] Read current/monitor value properties from an existing PLC watch table only. It does not create/modify watch tables, write PLC values, go offline, or use force operations. |
| PlanOnlineReadOnlyMonitoring | [L2][Online-Monitoring] Validate an online-monitoring request shape without connecting to TIA Portal. Read-only preflight only: no go-online/offline, no watch-table modification, no value write, and no force operation. |
| PlanOnlineReadOnlyDataProvider | [L2][Online-Monitoring] Plan the commercial current-value path through an external read-only data provider such as opcua or s7-readonly. This is a preflight only: it does not connect, write PLC values, modify watch tables, go online/offline through TIA, or use force operations. |
| ProbeS7CpuIdentity | [L2][Online-Monitoring] Read-only: connect to a physical CPU over the S7 protocol (ISO-on-TCP, port 102) and read its identification (module type, serial, names). Does NOT write, force, or change CPU mode. Use this first to confirm the IP belongs to the intended PLC before reading values. S7-1200/1500 use rack 0, slot 1. |
| ReadPlcLiveValuesS7 | [L2][Online-Monitoring] Read-only FAST live values from a physical CPU over the S7 protocol (port 102), independent of TIA Openness. Give absolute S7 addresses (DB10.DBD0:REAL, DB1.DBX2.3, M0.0, MW12, DB5.DBD8:DINT). Returns current values in one round-trip (typically tens of ms). NEVER writes/forces. Preconditions for S7-1200/1500: enable 'Permit PUT/GET access' on the CPU and read NON-optimized DBs (M/I/Q are unrestricted). Use expectModuleContains to hard-guard the target identity. |
| SamplePlcLiveValuesS7 | [L2][Online-Monitoring] Read-only TREND sampling over the S7 protocol (port 102): on one open connection, read a set of absolute S7 addresses every intervalMs for up to durationMs (hard-capped at 120 s / 5000 samples), returning a time series per address plus min/max/avg of the numeric ones. Ideal for capturing a PID step response or watching a signal move over time. NEVER writes/forces. The call BLOCKS for ~durationMs while it samples. Same address syntax and identity guard as ReadPlcLiveValuesS7. |
| TraceTagCause | [L2][Online-Monitoring] Answer 'why is tag X this value / what sets it' by static analysis of the OFFLINE project. Read-only: exports code blocks to SimaticML and finds every network that WRITES the tag (LAD coils S/R/=, or StructuredText ':=' assignments) plus the gating condition operands in those networks. Cross-reference service is not needed. Then live-read the returned gatingConditions with ReadPlcLiveValuesS7 to see which condition is currently driving the value. Tip: pass blockScope to limit which blocks are scanned (faster). NOTE: block export needs the project OFFLINE in TIA — Openness cannot export blocks while it is connected online to the PLC. |
| TraceTagCauseLive | [L2][Online-Monitoring] Live causal trace: runs the offline TraceTagCause to find what writes a tag and its gating conditions, then resolves each gating operand to an absolute address via the PLC tag table and LIVE-READS the resolvable ones over S7 (port 102) — so you see which interlock/condition is currently TRUE and driving the value. Read-only. Operands that are DB members / optimized / symbolic have no absolute PLC-tag address and are returned unresolved (read those via OPC UA). Requires the CPU ip; for the offline-only trace use TraceTagCause. NOTE: the offline block export needs the project OFFLINE in TIA (Openness cannot export in online mode); the S7 live-read is a separate direct connection, unaffected by going offline. |
| ReadPlcLiveValuesOpcUa | [L2][Online-Monitoring] Read-only live values from a CPU's OPC UA server (default opc.tcp port 4840), independent of TIA Openness. Anonymous, no-security session; reads the Value attribute of each node. NEVER writes or calls methods. Node IDs use OPC UA syntax, e.g. 'ns=3;s=\.\' or 'i=2258'. Precondition: the CPU's OPC UA server must be enabled and the variables exposed (Runtime license on S7-1200/1500). If the server is off, connection is refused (reported cleanly). |
| GetPlcRunStateS7 | [L2][Online-Monitoring] Read-only: read the CPU operating mode (RUN / STOP / UNKNOWN) over the S7 protocol (port 102) — something TIA Openness cannot do. Also returns the CPU clock and a best-effort raw diagnostic-buffer dump (event IDs in hex + raw bytes; full event text needs TIA's text database, and SZL may be unavailable on some S7-1200 CPUs — reported cleanly). Never writes/forces/changes mode. |
| MonitorWatchTableLiveS7 | [L2][Online-Monitoring] Read-only: live-monitor an existing TIA watch table. Reads the table's entry addresses (Openness, read-only) and live-reads them over the S7 protocol (port 102). Returns name/address/value rows. Never edits the watch table, writes, or forces. Absolute-address entries (e.g. %DB1.DBW0, %MW4) are read; symbolic/optimized entries are listed as unresolved (use OPC UA for those). Identity guard via expectModuleContains. Use GetPlcWatchTables to list table names. |

### PLC-Alarms

| Tool | Description |
|---|---|
| ExportAlarmClasses | [L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject] Export PLC alarm classes to a file. Alarm classes define severity, acknowledgment behavior, and display colors for alarms. The exported file can be edited and re-imported to update alarm class configurations. Use before bulk alarm class updates to create a backup. |
| ImportAlarmClasses | [L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject] Import PLC alarm classes from a previously exported file. Overwrites existing alarm class definitions. Run CompileSoftware after import. |
| ExportAlarmTextLists | [L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject] Export all PLC alarm text lists to an XLSX (Excel) file. Text lists contain the text strings shown for each alarm condition. Supports multi-language projects — all configured languages are exported. Typical use: export → translate in Excel → ImportAlarmTextLists. |
| ImportAlarmTextLists | [L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject] Import PLC alarm text lists from an XLSX file. The file must match the format exported by ExportAlarmTextLists. Run CompileSoftware after import to validate alarm configuration. |
| ExportAlarmInstanceTexts | [L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject] Export PLC alarm instance texts to an XLSX file. Instance texts are the alarm messages tied to specific FB/FC instances (e.g. Motor_01.AlarmText). Options control what additional columns are included in the export. Typical use: export → fill in alarm descriptions → ImportInstanceTexts (not yet exposed — edit via TIA Portal UI). |

### PLC-Builders

| Tool | Description |
|---|---|
| BuildPlcUdtXml | [L2][PLC-Builders][Offline] Build a TIA V21 PLC UDT/PlcStruct XML document from structured JSON. Input: {members:[{name,datatype,externalWritable?,commentZhCn?}]}. It only returns XML; it does not connect to TIA Portal, import types, write files, or modify projects. |
| BuildPlcTagTableXml | [L2][PLC-Builders][Offline] Build a TIA V21 PLC tag table XML document from structured JSON. Input: {tableName,tags:[{name,dataTypeName,logicalAddress}]}. It only returns XML; it does not connect to TIA Portal, import tag tables, write files, or modify projects. |
| BuildPlcGlobalDbXml | [L2][PLC-Builders][Offline] Build a TIA V21 PLC GlobalDB XML document from structured JSON. Input: {dbName,dbNumber,staticMembers:[{name,datatype,externalWritable?,commentZhCn?,startValue?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects. |
| BuildStructuredTextXml | [L2][PLC-Builders][Offline] Build a TIA V21 StructuredText/v4 XML fragment from operation JSON. Input: {operations:[{op:'if'\|'else'\|'endif'\|'assignment'\|'token'\|'blank'\|'newline', ...}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects. |
| BuildFlgNetCallXml | [L2][PLC-Builders][Offline] NARROW SCOPE: builds ONLY a LAD network that calls one FC with parameters. For general ladder (contacts/coils/SR/compare/Move/math) author S7DCL text and import with ImportBlocksFromDocuments — there is no XML builder for those, and hand-written FlgNet XML is the usual cause of import errors. Build a TIA V21 LAD FlgNet/v5 FC call network XML from structured JSON. Input: {callName,parameters:[{name,section,dataType,sourceKind?,symbolPath?\|symbol?\|value?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects. |
| ComposePlcFcBlockXml | [L2][PLC-Builders][Offline] Compose a TIA V21 SCL FC block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs:[{name,datatype}],outputs:[{name,datatype}],structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects. |
| ComposePlcFbBlockXml | [L2][PLC-Builders][Offline] Compose a TIA V21 SCL FB block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs?,outputs?,inouts?,statics?,temps?,structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, create instance DBs, or modify projects. |
| ComposePlcLadFcBlockXml | [L2][PLC-Builders][Offline] NARROW SCOPE: every network must be an FC call; this cannot emit contacts/coils/SR/compare/Move/math. For general ladder, author S7DCL text (.s7dcl + .s7res) and import with ImportBlocksFromDocuments instead. Compose a TIA V21 LAD FC block XML containing one or more FlgNet/v5 FC-call networks. Each network is an FC call described as { callJson: { callName, parameters[] }, titleZhCn?, commentZhCn? }. Top-level: blockName, blockNumber, optional inputs/outputs members, optional commentZhCn / titleZhCn. Returns XML only; does not connect to TIA Portal or import. Pair with ImportBlock. |
| BuildPlcSymbolManifestFromXmlPath | [L2][PLC-Builders]Offline-only helper: extract a PLC symbol manifest from PLC tag table and GlobalDB XML files or directories. It does not connect to TIA Portal or modify projects. |

### PLC-Online

| Tool | Description |
|---|---|
| GetPlcForceTables | [L2][Category:PLC-Online][PreCondition:Connect+OpenProject] List all force table names in the PLC software. Force tables configure which variables are continuously forced to specific values while the CPU is online. Read-only: this server exposes NO tool for creating or editing force entries — forcing overrides live PLC logic (a forced output stays forced regardless of what the program writes) and is deliberately kept out of the AI tool surface. Create or edit force entries in the TIA Portal UI. For a one-shot value written from a watch table instead, use SetWatchTableModifyValue (the variable reverts to PLC logic afterwards). |
| SetWatchTableModifyValue | [L2][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+GoOnline] Configure a watch table entry to write a value to a PLC variable once (or on a trigger). This is an OFFLINE CONFIGURATION step — the value is written to the PLC only when TIA Portal is online and the trigger fires. Trigger options: Permanent (every cycle), PermanentAtStart (every cycle, at scan start), OnceOnlyAtStart (single write at scan start), PermanentAtEnd, OnceOnlyAtEnd, OnceOnlyAtStop. Use GoOnline before calling this for the write to reach the PLC. SAFETY: the target is a variable in the physical CPU, not a simulation. The moment the trigger fires the value lands on the real address — if that address is a coil, a valve, a contactor or a drive enable, the machine moves at that instant, with no acknowledgement step. Before calling, know exactly what the address drives and confirm nobody is at or inside the machine. The resulting physical motion is NOT undone by calling this tool again with another value; the equipment stays wherever it moved to. Not a Force: this writes the value once per trigger event and the variable then follows PLC logic again (a force would keep overriding the program continuously). This server exposes no tool for force entries — GetPlcForceTables only lists them; use TIA Portal directly to force a value. Example: SetWatchTableModifyValue('PLC_1', 'Debug_WT', 'DB1.DBX0.0', 'TRUE', 'OnceOnlyAtStart') |
| CompareSoftwareToOnline | [L2][Category:PLC-Online][PreCondition:Connect+OpenProject+GoOnline] Compare the offline PLC software in the project against the program currently running on the physical CPU. Use after editing blocks to confirm what differs from the live CPU before downloading, or after a download to verify offline/online consistency. Returns a tree-walked list of differences (only entries where ComparisonResult is not 'Equal' are reported). Requires GoOnline to be called first; will return IsOnline=false with guidance otherwise. |

### PLC-OpcUA

| Tool | Description |
|---|---|
| GetOpcUaConfig | [L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject] Read the full OPC UA server configuration for a PLC: server interfaces, SIMATIC interfaces, and reference namespaces — each with their Name, Enabled state, and key properties. Use this to audit what OPC UA interfaces exist before enabling or exporting them. Enabled=true means the interface is active and will be downloaded to the CPU. |
| SetOpcUaInterfaceEnabled | [L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject] Enable or disable an OPC UA server interface, SIMATIC interface, or reference namespace. Setting Enabled=true activates the interface — download to PLC is required for the change to take effect on the CPU. interfaceType options: 'ServerInterface' (default), 'SimaticInterface', 'ReferenceNamespace'. Workflow: GetOpcUaConfig → SetOpcUaInterfaceEnabled → DownloadToPlc. |
| ExportOpcUaInterface | [L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject] Export an OPC UA server interface or reference namespace to an XML file. The exported XML can be inspected, modified, and re-imported. interfaceType: 'ServerInterface' (default), 'SimaticInterface', 'ReferenceNamespace'. |
| ImportOpcUaInterface | [L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject] Import an OPC UA server interface or reference namespace from an XML file. If an interface with the same name (derived from the file name) already exists, it is updated in place. Otherwise a new interface is created. Download to PLC after import to apply changes to the CPU. interfaceType: 'ServerInterface' (default), 'ReferenceNamespace'. |

### PLC-Software

| Tool | Description |
|---|---|
| GetBlockInfo | [L2][PLC-Software] Get detailed info for one block (attributes, language, number, modification time). Requires: Connect + OpenProject. blockPath must be fully qualified: 'Group/Subgroup/BlockName' — get it from GetSoftwareTree or GetBlocksWithHierarchy. Returns: IsConsistent (false = must compile before export). |
| GetBlocks | [L2][PLC-Software] Get a flat list of all blocks in PLC software. Requires: Connect + OpenProject. Use GetBlocksWithHierarchy instead when you need group/folder paths for ExportBlock. Returns: block name, number, type (OB/FC/FB/GlobalDB/InstanceDB), programming language. |
| GetBlocksWithHierarchy | [L2][PLC-Software]Get a list of all blocks with their group hierarchy from the plc software. |
| ExportBlock | [L2][PLC-Software] Export one block to an XML file. Requires: Connect + OpenProject + block must be consistent (compile first if IsConsistent=false). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree — bare names return InvalidParams with suggestions. Pick the right tool: batch → ExportBlocks; readable SCL/.s7dcl text → ExportAsDocuments. |
| ImportBlocksFromDirectory | [L2][PLC-Software] Batch import PLC block .xml (SimaticML) files from a directory into a block group. Pick the right tool: SCL/.s7dcl text → ImportBlocksFromDocuments; a full mixed program with UDTs+tag tables+blocks auto-ordered → ImportPlcProgramFromDirectory; a single XML file → ImportBlock. |
| ImportPlcProgramFromDirectory | [L2][PLC-Software] HIGH-LEVEL batch import tool. Recursively scans a directory for PLC XML files, auto-classifies them as UDT/TagTable/Block, imports in correct dependency order (UDTs first, then tag tables, then blocks), and optionally compiles. Requires: Connect + OpenProject. Best for importing a full exported PLC program or a set of generated XML blocks. |
| RepairAndReimportBlock | [L2][PLC-Software]Try import a block XML; if compile fails, return diagnostics and best-effort suggestions (no destructive actions). |
| ExportBlocks | [L2][PLC-Software] Export all (or regexName-filtered) blocks to a directory as SimaticML XML. Pick the right tool: readable SCL/.s7dcl text → ExportBlocksAsDocuments; a single block → ExportBlock. |
| DeletePlcBlock | [L2][PLC-Software][Destructive] Preview or delete exactly one PLC block by its exact path, including blocks inside nested groups. THIS IS ALSO THE TOOL FOR DELETING A DATA BLOCK: global DB, instance DB, ARRAY DB, FB, FC and OB are all PLC blocks, so there is no separate DeleteGlobalDb / DeleteDb / DeleteFunctionBlock tool - use this one. Defaults to dryRun=true, which changes nothing and reports the resolved target plus its cross references. It never deletes instance DBs or callers automatically. Before dryRun=false, review the previewed cross references (or run GetCrossReferences) and back the block up with ExportAsDocuments; compile with CompileSoftware after deletion and before SaveProject. Regex and wildcards are rejected. To delete a tag table use DeletePlcTagTable, a UDT use DeletePlcType. |
| DeletePlcTagTable | [L2][PLC-Software][Destructive] Preview or delete ONE PLC tag table (variable table / tag list) by name, including tables nested in user groups. Defaults to dryRun=true, which only reports what the table contains and deletes nothing. DANGER: deleting a tag table removes the SYMBOLS of every tag in it. HMI panels bind PLC tags by symbolic name, so the PLC may still compile clean while the HMI silently loses its bindings - always review the previewed tag list and cross references first. The default tag table (IsDefault) is refused. Cross references are attempted but may be unavailable at tag-table level; the response says explicitly whether they were obtained. Regex and wildcards are rejected. Back up first with ExportPlcTagTable. |
| DeletePlcType | [L2][PLC-Software][Destructive] Preview or delete ONE PLC user data type (UDT / PlcType) by its exact path. Defaults to dryRun=true. Deleting a UDT breaks every DB and block interface declared with it, so the preview reports its cross references (GetCrossReferences works at type level) before you commit - review them first. Regex and wildcards are rejected. Export the type first with ExportType, and CompileSoftware afterwards. |
| ExportAsDocuments | [L2][PLC-Software] PREFERRED on V21+ for exporting one block. Exports a single program block to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML (ExportBlock). Requires TIA Portal V20 or newer. |
| ExportBlocksAsDocuments | [L2][PLC-Software] PREFERRED on V21+ for batch export. Exports multiple program blocks to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML. Requires TIA Portal V20 or newer. |
| ImportFromDocuments | [L2][PLC-Software] PREFERRED on V21+ for importing one block. Imports a single program block from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer. After import it reads back to confirm the block is present (Meta.verified). |
| ImportBlocksFromDocuments | [L2][PLC-Software] PREFERRED on V21+ for batch import. Imports multiple program blocks from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer. |
| CreatePlcBlockGroup | [L2][PLC-Software] Create a (nested) program-block group/folder, creating any missing parent groups along the path. Idempotent — returns ok if the group already exists. groupPath uses '/' separators relative to 'Program blocks' root, e.g. '01_手动控制/手动意图'. Requires: Connect + OpenProject. Use this to set up the 手动/手自动接口/自动 layer folders, then MoveBlockToGroup to organize blocks into them. |
| MoveBlockToGroup | [L2][PLC-Software] Move/organize an existing block into a program-block group (found anywhere by exact name). Openness cannot reparent a block, so this exports the block, deletes it, and re-imports it into the target group (SIMATIC SD .s7dcl preferred, SimaticML XML fallback for STL/mixed-language). The block number and references are preserved. autoCreateGroup creates the target group path if missing. Requires: Connect + OpenProject + block consistent (compile first). After moving, call CompileAndDiagnosePlc to confirm 0 errors. Note: avoid moving OBs with event bindings via this round-trip. |
| GetCrossReferences | [L2][PLC-Software]Get cross references for a Step7 block/type (best-effort). Requires applicable object and Openness support. |
| GetPlcExternalSources | [L2][PLC-Software]List PLC external source names (best-effort) |
| GetPlcTagTables | [L2][PLC-Software] List all PLC tag table names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). Use before ExportPlcTagTable to get exact table names, or before ImportPlcTagTable to check for conflicts. |
| ExportPlcTagTable | [L2][PLC-Software]Export one PLC tag table (PlcTagTable) to XML file |
| ImportPlcTagTablesFromDirectory | [L2][PLC-Software]Batch import PLC tag table .xml files from a directory (best-effort) |
| GetPlcWatchTables | [L2][PLC-Software]List PLC watch/monitor table names (PlcWatchTable). Read-only. |
| ExportPlcWatchTable | [L2][PLC-Software]Export one PLC watch/monitor table (PlcWatchTable) to XML file. Read-only against the TIA project. |
| ExportPlcWatchTablesToDirectory | [L2][PLC-Software]Export all PLC watch/monitor tables to XML files. Read-only against the TIA project. |
| ImportTechnologyObject | [L2][PLC-Software]Import one PLC Technology Object XML file into PLC software (best-effort) |
| ImportTechnologyObjectsFromDirectory | [L2][PLC-Software]Batch import PLC technology object .xml files from a directory (best-effort) |
| ImportPlcExternalSource | [L2][PLC-Software]Import one PLC external source file into a group (best-effort) |
| DeletePlcExternalSource | [L2][PLC-Software]Delete a PLC external source by name so ImportPlcExternalSource can replace it (idempotent). Name may include or omit .scl. |
| GenerateBlocksFromExternalSource | [L2][PLC-Software]Generate blocks from a PLC external source by name (best-effort) |
| GetTypeInfo | [L2][PLC-Software]Get a type info from the plc software |
| GetTypes | [L2][PLC-Software]Get a list of types from the plc software |
| ExportType | [L2][PLC-Software]Export a type from the plc software |
| SeedProjectFromReference | [L2][PLC-Software]Seed PLC blocks/types and HMI screens/tagtables from a reference directory (manifest.json + {{PLACEHOLDER}} replace) |
| ExportTypes | [L2][PLC-Software]Export types from the plc software to path |

### PLC-TechnologyObjects

| Tool | Description |
|---|---|
| GetTechnologyObjects | [L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject] List all Technology Objects (TOs) in the PLC software: axes, cams, measuring inputs, etc. Returns each TO's Name, type (OfSystemLibElement), and firmware version (OfSystemLibVersion). Use this to discover TO names before ExportTechnologyObject. No tool returns axis/TO parameter values directly — export the TO with ExportTechnologyObject and read the XML file. TOs are stored as TechnologicalInstanceDB instances in the TechnologicalObjectGroup. |
| ExportTechnologyObject | [L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject] Export a single Technology Object (axis, cam, measuring input, etc.) to an XML file. The XML can be inspected, modified offline, and re-imported with ImportTechnologyObject. Use GetTechnologyObjects first to confirm the exact TO name. |
| ExportTechnologyObjectsToDirectory | [L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject] Batch-export all (or regex-filtered) Technology Objects to XML files in a directory. Each TO is saved as '<TOName>.xml'. Returns lists of exported names and any failures. Use regexName to filter by TO name, e.g. 'Axis_.*' for all axes. |

### Project

| Tool | Description |
|---|---|
| SaveAsProject | [L2][Project]Save current TIA-Portal project/session with a new name |

### Reflection

| Tool | Description |
|---|---|
| DescribeObject | [L2][Reflection]Describe an Openness object via reflection. Use this first when a natural-language TIA operation has no direct MCP tool. objectKind: Project\|Portal\|Device\|DeviceItem\|Software\|Block\|Type\|HmiScreen\|HmiTag\|HmiScreenItem |
| GetObjectProperty | [L2][Reflection]Get an Openness object property by dotted path. Use after DescribeObject/DescribeObjectProperty to safely inspect current state before writing. |
| ListObjectChildren | [L2][Reflection]List child items from an enumerable Openness property, e.g. Devices, DeviceItems, Connections, Screens, Blocks. Use to discover paths instead of guessing. |
| InvokeObject | [L2][Reflection]Invoke an Openness method via reflection. Default is read-oriented; set allowWrite=true only after DescribeObject confirms the target method/signature. This is the generic bridge for public API operations not yet wrapped by MCP. |
| DescribeService | [L2][Reflection]GetService bridge: describe a service object (by type name suffix) from a target object. |
| InvokeService | [L2][Reflection]GetService bridge: invoke a method on a service object (by type name suffix) from a target object. |
| DescribeObjectProperty | [L2][Reflection]Describe an object's nested property via reflection (members list). propertyPath supports dotted path. |

### Reports

| Tool | Description |
|---|---|
| BuildReleaseDiagnosticReport | [L2][Reports]Build an offline diagnostic report from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects. |
| BuildReleaseRunbook | [L2][Reports]Build an offline first-user runbook from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects. |
| BuildReleaseManifest | [L2][Reports]Build an offline machine-readable release manifest from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects. |
| RebuildReleaseHandoffArtifacts | [L2][Reports]Rebuild diagnostics, runbook, and manifest files from an existing OfflineReleaseValidationSuite JSON report. Offline-only and does not connect to TIA Portal. |

### Validation

| Tool | Description |
|---|---|
| RunOfflineReleaseValidationSuite | [L2][Validation]Offline-only helper: run the release smoke suite covering PLC Builder, Classic HMI, PLC symbol extraction, Unified HMI template layout, HMI action recipes, and online-monitoring safety guardrails. It does not connect to TIA Portal or modify projects. |
| RunV2PlanCompletionAudit | [L2][Validation]Offline-only strict audit for docs/TIA_MCP_常见操作全覆盖方案_V2_二次优化计划.md. It reports verified hard-gate percentage and blocks 100% claims when real TIA/online evidence is missing. |
| RunHmiTemplatePlcSyncPrecheckSuite | [L2][Validation]Offline-only helper: verify Unified HMI template RequiredTags against real PLC tag/DB-member XML symbols before any HMI binding. It does not connect to TIA Portal or modify projects. |

### VersionControl

| Tool | Description |
|---|---|
| CreateVersionControlWorkspace | [L2][VersionControl] Create a VCI workspace pointing at a folder on disk — normally the working tree of a Git repository, so every synchronized export lands where Git can commit it. Creating the workspace does NOT map any objects into it — call ConnectProjectToWorkspace afterwards to map the whole project (or one device) automatically. Requires TIA V21+ and an open project. |
| ConnectProjectToWorkspace | [L2][VersionControl] Put a WHOLE project under version control automatically - no TIA UI clicks. Walks the project tree, asks every object whether VCI can map it (Workspace.GetSupportedFileFormats) and maps each supported object with Workspace.ConnectObject. COARSE-FIRST: when a device or PLC software object is mappable as one unit it is mapped whole and its children are not visited, so you get the fewest mappings that still cover everything. Objects VCI does not support (typically hardware configuration) are reported, never silently dropped. DEFAULTS TO dryRun=true: the default call only reports what it WOULD map. After a real run call SyncVersionControlWorkspace(ProjectToWorkspace, dryRun=false), then git commit. |

