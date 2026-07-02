# Change Log

## [2.2.7] - 2026-07-02 - 门槛归零：版本自路由 + 四宿主一键配置 + 模型引导（instructions/GetAuthoringGuide）

本轮全部围绕两个真实用户痛点：①配置要人肉填 MCP 路径/博途路径/版本（issue #9）②非 Claude 的 AI 调用时生成代码质量差、耗时长。

- **版本自路由（修 issue #8 根因）**：exe 启动时若实际 TIA 版本 ≠ 本 exe 编译目标（V20↔V21），自动找到旁边的兄弟 exe（`bin`↔`bin-v20`、`runtime/v20`↔`v21` 两种布局都认）并以相同参数重新执行，stdio 继承——MCP 宿主和 CLI 都无感。V20 机器拿到 V21 exe 不再报「未能加载 Siemens.Engineering.Base 21.0」，而是照常工作。防死循环 env 守卫；找不到兄弟 exe 时给出明确指引。
- **修复多版本机器上的程序集解析劫持**：`TiaPortalLocation` 环境变量是版本无关的（常指向 V21 安装），旧逻辑让它优先于版本相关注册表，导致 V20 exe 在 V20+V21 双装机器上报「Could not find DLL 'Siemens.Engineering' for version 20」。现在：环境变量路径版本匹配才采用 → 版本专属注册表 → 最后才兜底环境变量。本机双装环境实测修复。
- **`tia config` 一键配置扩到 4 宿主（修 issue #9）**：Claude Desktop / **Claude Code**（`~/.claude.json`）/ Cursor / **VS Code**（`%APPDATA%\Code\User\mcp.json`，`servers`+`type:stdio` 专属 schema）。自动发现自身路径 + 注册表版本 + **版本匹配的 exe**；默认只写本机检测到的宿主（不给没装的 IDE 凭空造配置）；`--host claude|claude-code|cursor|vscode` 指定单个；`--print` 同时输出两种 schema 片段；JSON 不再把中文路径转成 `\uXXXX` 转义。原配置 `.bak` 备份、其它 server 保留（沿用已真机验证的合并逻辑）。
- **模型引导（针对“其它 AI 生成质量差”）**：
  - **MCP initialize 下发 `instructions`**：黄金路径（整工程→ScaffoldProject+dryRun；改块→s7dcl/SCL 文本导入；禁止手写 FlgNet XML）、BOM 编码规则、编译/保存纪律、错误恢复纪律——所有 MCP 客户端（含从不读 SKILL.md 的 VS Code/Cursor/三方 agent）自动注入模型上下文。
  - **新工具 `GetAuthoringGuide(topic)`**［L0］：workflow / scl / lad / db / hmi / errors 六个主题的已验证语法速查（SCL 骨架与十诫、s7dcl LAD 文本要素、BOM 规则表、常见报错→精确修法），模型写代码前一次调用拿到全部约束。
  - **Bootstrap OperatingRules 补 2 条**：写代码前先调 GetAuthoringGuide；BOM 编码规则。
- README 中英：「挂载 MCP」改为一条 `config` 命令的全自动流程。
- 离线验证：V20/V21 双编译 0 错；V20 exe 真机自路由到 V21 exe 实测通过；`config --print` 双 schema 实测正确；MCP 握手实测 instructions 下发 + 199 工具含 GetAuthoringGuide + 主题内容正确。

## [2.2.6] - 2026-07-02 - 三大 god-file 拆分为领域 partial + CLI describe 增强

维护性重构 + CLI 易用性，工具集与行为不变（重构后 exe 已日常真机使用验证）：

- **god-file 拆分**：`McpServer.cs`(-7000行) / `Program.cs`(-7784行) / `Portal.cs`(-13221行) 按领域拆成 18 个 partial 文件（Blocks/Devices/Documents/Groups/PlcSoftware/ProjectSession/Types、CliProbes/HmiTemplates/PlcHmiSyncXml/ReportBuilders、Alarms/Download/Helpers/OpcUa/Software 等），并发协作不再撞车，行为零变化。
- **CLI `tia describe` 增强**：输出真实项目树 + 块清单（类型/名称/语言），此前只打印状态串。
- **Claude Code 插件清单**：新增 `.claude-plugin/` + `.mcp.json` + `runtime/v21` 捆绑运行时，支持 `/plugin marketplace add` 一键安装。
- 版本对齐：V20/V21 双 exe、manifest、README 统一 2.2.6。

## [2.2.5] - 2026-06-16 - 读组态IP + 块路径修复 + softwarePath 容错 + 文档诚实化

继续降门槛 / 提正确率 / 成熟化，改动均经真机或离线验证：

- **新增只读工具 `ExportDeviceAml`**：导出设备硬件组态为 AutomationML(CAx) `.aml`，内含**组态 IP / 子网 / 网关 / PROFINET 设备名**——补上 `GetDeviceItemNetworkInfo` 读不到已组态 IP 的缺口。真机验证（江夏 安全PLC / S7-1200 station_3）：读出 IP=192.168.0.32 / 网关 .254 / 掩码 255.255.255.0。
- **修复块路径解析**：`GetBlockInfo` 等对根级块自动跳过开头的 `Program blocks`/`程序块` 根容器段——裸名 `FR12E02v2` 与带前缀 `Program blocks/FR12E02v2` 现在都能命中（此前加前缀报 "Block not found"）。真机验证通过。
- **`softwarePath` 容错解析**：精确匹配失败时自动兜底——容忍多余空格/大小写、单 PLC 工程任意 token 自动认到唯一 PLC、唯一子串匹配（`"PLC"`→`"PLC_1"`、`"安全"`→`"安全PLC"`）；仍无法解析时列表工具（`GetPlcTagTables`/`GetPlcExternalSources`/`GetPlcWatchTables`）报错附 `Available PLC paths: …`。纯匹配逻辑 `Guard.MatchPlcName` 16 个离线用例全过。
- **下载 V21 cast bug 修复 + 真机验证**：`DownloadToPlc` 旧版在 V21 报"ConnectionConfiguration 无法转换为 IConfiguration"。修复：导航到 `ConfigurationTargetInterface`（它才是 IConfiguration）并应用路由；修正 StopModules 选择枚举为 `StopAll`（旧值"StopModule"不存在→每次下载"unhandled"中止）；反射异常解包显示真实原因。**真机验证（江夏 安全PLC / S7-1200）：state=Success，0 错**。
- **文档诚实化**：本轮先发现上一轮未提交文档把"下载已修复"等写在了代码之前，已逐处核对——softwarePath 容错按代码兑现、下载按真机验证转正，措辞与代码/真机一致。
- 工具数 189 → 190（仅新增 `ExportDeviceAml`）。
- **文档与既定方向对齐（竞品对标后）**：路线图（`docs/server-maturity-roadmap.md`）与 SKILL §18 中“安全F块 / PLCSIM / 原生Git-VCI”由“P1 待补缺口”改标为**主动放弃（低收益＋高风险）**，避免文档诱导去做已否决的功能；OPC UA 明确保持只读。统一工具数表述为 ~190(full) / ~38(lite)，订正 SKILL.md 中 184 / 196 / 180 等不一致旧值（精确以 `tools/list` 为准）。纯文档，无代码改动。

## [2.2.4] - 2026-06-15 - 一键配置：把 MCP 注册进 AI 宿主，零手改 JSON

降低上手门槛。新增内置一键配置，**无需第三方软件、无需手改配置文件**：

- **新增 CLI 子命令 `tia config`**：自动把 `tia-portal` 注册进 **Claude Desktop / Cursor** 的配置文件——自动写入**正确的 exe 绝对路径**（不再有 `REPLACE_ME`）与**自动检测的 TIA 版本**；**合并**进现有配置（保留你已有的其它 MCP server），原文件自动备份为 `*.bak`。
  - `tia config`：写入所有已知宿主；`--host claude|cursor` 指定其一；`--print` 只打印片段供手动粘贴（如 Claude Code）。
- **双击脚本**：交付包根目录新增 `配置MCP.bat`（V21）/ `配置MCP-v20.bat`（V20），双击即配，窗口显示结果。
- 取代原先需要手改 `cursor-mcp.example.json` 里 `REPLACE_ME` 的繁琐步骤。
- 纯新增，不改动现有工具与行为；工具数不变（189）。

## [2.2.3] - 2026-06-15 - 笨 AI 健壮性：自愈连接 + 可恢复错误指引 + 操作规则

> 注：v2.2.2 版本号已被既有的「S7DCL skill 文档」Release 占用，故本代码版本顺延为 2.2.3。

针对"搭载 MCP 后较弱的 AI 驱动仍会出 bug"的反馈，从源头降低出错与卡死，**工具数不变（189）**：

- **自愈连接/绑定**：`Portal.IsProjectNull()`（99 个调用点的集中判定）现会在判定"无工程"前尝试自愈——未连接但本机已有 TIA 进程在运行时自动附加并绑定其已打开的工程；已连接但未绑定时自动重绑；本机无 TIA 在运行则不擅自启动（避免缓慢失败），直接落到可执行提示。修复了"AI 忘记先 Connect/Attach 就调用工具"导致的原始崩溃。
- **可恢复的错误指引**：新增 `McpHints.Recovery()` 异常翻译器，按内部异常链识别 7 类常见失败（未连接 / 无工程 / 工程已被打开 / 名称路径错→提示查 GetProjectTree·GetSoftwareTree / TIA 版本不匹配 / Openness 用户组 / know-how 保护），统一注入到 185 处通用 `catch`（McpServer 175 + Patch 2 + Runtime 8）。错误信息从"只报原始异常"变为"告诉 AI 下一步该做什么"。无法识别时不加噪声。
- **首调操作规则**：`Bootstrap` 响应新增 `OperatingRules`（5 条：调用顺序 / 写后必须 Compile+Save / 名称要精确 / 读错误信息照做 / 大任务用 ScaffoldProject）。即使宿主不加载 SKILL.md，AI 第一次调用就能拿到正确用法。
- **更清晰的前置错误**：12 处误导性的 `"No project is open in TIA Portal"` 改为可执行指引（调用 AttachToOpenProject / OpenProject / CreateProject）。

## [2.2.1] - 2026-06-09 - v2.2.0 三工具真机验证 + 因果溯源在线模式消息修复

v2.2.0 的 3 个在线监控工具已在真机（CPU 1211C @ 192.168.0.32）端到端验证通过：

- **`GetPlcRunStateS7`** — 读到 `RUN`；该 1211C 的 SZL 诊断缓冲不可用时按设计干净降级（仍给出 RUN/STOP）。
- **`SamplePlcLiveValuesS7`** — 采样 DB34 心跳/标志，确认时间序列与 min/max/avg 正确（心跳随时间递增）。
- **`TraceTagCauseLive`** — 离线追溯 `EMG_STOP`，正确命中其在 `Crane_Communication` 网络4 的赋值及两个门控条件，并正确判定门控操作数为 DB 成员（无绝对地址→提示用 OPC UA 读）。

**修复（`TraceTagCause` / `TraceTagCauseLive`）：** 当 TIA 与 PLC **在线连接**时，Openness 无法导出块（`Export ... not supported in online mode`）。此前所有块导出失败仍报 `No block writes 'X'`，会误导为“解析后无写入”。现新增 `analyzedBlockCount`，当无任何块成功导出时给出 `INCONCLUSIVE` 并提示“在 TIA 转至离线后重试（S7 实时读为独立直连，不受影响）”。两工具 Description 同步补充此前置条件。工具数不变（189）。

## [2.2.0] - 2026-06-09 - 在线监控扩展（趋势采样 / 实时因果溯源 / RUN-STOP 状态）

延续 v2.1.0 的运行时只读通道，新增 3 个工具（全部 `[L2]`，纯只读），工具数 186→189：

- **`SamplePlcLiveValuesS7`** — S7 趋势采样：单连接对一组绝对地址按 `intervalMs` 采样至 `durationMs`（封顶 120s / 5000 点），返回每个信号的时间序列 + min/max/avg。**直接用于抓 PID 阶跃响应曲线**。调用会阻塞 ~durationMs。
- **`TraceTagCauseLive`** — 实时因果溯源：先跑离线 `TraceTagCause` 找写入网络+门控条件，再把门控操作数**经 PLC 变量表解析成绝对地址**并 S7 实时读，告诉你**此刻是哪个互锁/条件在驱动**。DB 成员/优化/符号量无绝对地址→标 unresolved（改用 OPC UA 符号读）。
- **`GetPlcRunStateS7`** — 读 CPU 运行模式 RUN/STOP/UNKNOWN（**Openness 做不到**），附 CPU 时钟 + 尽力读诊断缓冲原始条目（事件ID hex + 原始字节；完整事件文本需 TIA 文本库，部分 S7-1200 的 SZL 不可用时干净报错）。
- 三者均纯只读（不写/不强制/不改运行模式），响应带 `safety` 自证。
- `tool-capability-matrix.md` 生成脚本修正为扫描全部 `McpServer*.cs` 分部文件（含 `McpServer.Runtime.cs`），矩阵=189。
- 13 项确定性单测覆盖地址解析（`TiaTagToSpec`）与诊断缓冲解析（`ParseSzlDiagRecords`）等纯逻辑；双构建 V20/V21 均 0 错，runtime tools/list 实测 189。真机端到端验证待现场放行 PLC。

## [2.1.0] - 2026-06-09 - 在线只读实时读值（S7 / OPC UA / 监控表 / 离线溯因）

TIA Openness 是工程接口，**读不到运行中 CPU 的实时值**。本版新增一条独立于 Openness 的**运行时只读通道**，直连 CPU 读实时值。新增 5 个工具（全部 `[L2]`，**纯只读：不写、不强制、不改运行模式**），工具数 181→186。

### 新增 — 运行时只读实时读值

- `ReadPlcLiveValuesS7` / `ProbeS7CpuIdentity` — S7 协议（ISO-on-TCP, 端口 102），绝对地址 `DB34.DBD116:DINT`/`M0.0`/`IW76` 等，单次几十~几百 ms。`expectModuleContains` 身份护栏（型号正向不匹配才中止）。S7-1200/1500 需开启 PUT/GET 且读非优化 DB（M/I/Q 不受限）。
- `ReadPlcLiveValuesOpcUa` — OPC UA（端口 4840，匿名无加密），**会话按 endpoint 缓存复用**（首次 ~1.7s，之后 ~150-220ms，返回 `reusedSession`），会话失效自动重建一次；对锁的等待有界，避免不可达服务器导致后续调用堆积。
- `MonitorWatchTableLiveS7` — 经 Openness 取已有监控表条目地址（只读）+ S7 实时读值；按 TIA `DEC_signed` 显示格式映射为有符号 `INT/DINT/SINT`，有符号量不再被误显为大正数。符号/优化条目列为 unresolved（改用 OPC UA）。
- `TraceTagCause` — **离线静态溯因**：导出代码块解析 SimaticML（LAD 线圈 S/R/= 与 ST 的 `:=`），找出写入该变量的网络及门控条件操作数，再用 `ReadPlcLiveValuesS7` 实时读这些条件判断当前由谁驱动。不联机、无需交叉引用服务。
- 每个响应都带 `safety` 自证字段（`readOnly/writesValues/usesForce/changesCpuMode` 全 false）。
- 真机验证（安全PLC, CPU 1211C @192.168.0.32）：S7 / OPC UA / 监控表三法与博途显示逐字节交叉核对一致。
- 新增中文使用指南 `docs/在线实时读值_使用指南.md`。

## [2.0.2] - 2026-06-08

V20 兼容性修复小版本（修复 GitHub issue #2）。

### 修复 — V20 导入标签表报 `engineering version 'V21' not supported`

- `Portal.ImportPlcTagTable` 现在统一走 `PrepareXmlForImport`，把硬编码的 `<Engineering version="V21"/>` 头部改写为当前连接的博途版本（并补 UTF-8 BOM）。此前块/类型导入已做此处理，唯独标签表漏过滤，导致 V20 上 `PlcBuildAndImport` 生成的变量表被 Openness 拒绝。

### 新增 — `WritePlcSclSourceFile`（离线，第 181 个工具）

- 把 SCL 源文本写成本地 `.scl` 外部源文件（**UTF-8 带 BOM**，中文注释不乱码）。不连接博途、不导入，只落盘并返回路径与手动导入指引。
- 作为 V20 用户的稳妥后路：当 FC 逻辑块 XML 因 `Cannot create SW.Blocks.CompileUnit... token not supported`（V21 SimaticML 令牌）被 V20 拒绝时，改用博途「外部源文件 → 从源生成块」手动导入，绕开严格的 XML 令牌校验。

## [2.0.1] - 2026-06-04

安全与隐私小版本（仅 `Program.cs`，无行为变更、无新工具）。

### 安全 — 修复 XML 外部实体（XXE）

- 导入/解析 XML 工程产物（块、画面、变量表、监控表）的 6 处 `XmlDocument.Load(...)` 之前统一设置 `XmlResolver = null`，禁用外部实体/DTD 解析。修复在 .NET Framework 4.8 上导入**第三方恶意 XML 文件**时可被 XXE（读取本地文件、SSRF、billion-laughs DoS）的风险。`XDocument.Load/Parse` 路径默认 `DtdProcessing.Prohibit`，本就安全，未改。

### 隐私 — 移除源码中的开发机个人路径

- 清除 `Program.cs` 里 11 处硬编码的作者机器路径（`C:\Users\<用户名>\...`），改为中性的 `Directory.GetCurrentDirectory()` / `MyDocuments` 兜底。这些只出现在 `--run-*` 开发者自测处理器的默认值中，不影响正常 MCP/CLI 运行。

## [2.0.0] - 2026-06-02

声明式 CLI 大版本：**同一个 exe 既是 MCP 服务，也是命令行**。任意 AI 产出一份 YAML/JSON spec，任意工程师跑一条命令即可从零建/改博途工程——**无需 MCP 客户端、无需安装、门槛最低**。底层完全复用现有引擎，不重写。

### 新增 — `tia` 命令行（薄层，复用现有引擎静态方法）

- **`tia gen <spec.yaml|json>`**：一条命令从 spec 生成完整工程（= ScaffoldProject）。`--dry-run` 离线校验、`--json` 机器可读输出。
- **`tia patch <spec.yaml|json>`**：把 spec **增量 upsert 到已有工程**（spec 内 `projectPath` 指向 .apXX）；spec 未提及的元素不动。`--no-overwrite` 保护手改的 LAD 代码块（UDT/DB/标签表始终按 spec 重同步）。
- **`tia compile / describe / export / import / prewarm / schema / version / help`**：编译诊断、工程树、导出/导入、常驻 headless 预热（原生子命令，去掉 Python 依赖）、spec 字段速查、版本号。
- **退出码契约**：0=成功 / 1=有失败步骤 / 2=错误，便于脚本与 AI 判读。

### 输入 — YAML + JSON 双解析

- 引入 YamlDotNet：`.json` 直通（AI 首选，零歧义），`.yaml/.yml` 解析并做标量类型推断；同一 spec 的 YAML 与 JSON 版产出一致（已离线验证）。

### 形态

- 仍是双 V20/V21 binary，仍为 MCP 服务（`tia` verb 之外的行为完全不变）；CLI 与 MCP 共享同一引擎，改一处两端受益。
- 离线验证通过：`gen`/`patch` 的 `--dry-run`、YAML/JSON 等价、退出码、`schema`/`version`/`help`。

### 2026-06-03 修订 — 实机验证 + 零基础上手优化

- **live 实机回归全过**（真连 TIA V21，headless）：`tia gen` 16/16 步、编译 Success 0 错；`tia prewarm` 后续命令 ~2s attach；`describe`/`compile`/`export`/`import`/`patch`(upsert 后再编译 Success) 全部 rc=0。
- **修复相对工程路径**：`tia describe/compile/export/import` 与 `tia patch` 的工程路径此前按 exe 目录解析（传相对路径会 `Projects.Open failed`），现按当前工作目录解析（`Path.GetFullPath`）。
- **新增 `tia` 命令入口**：交付包根目录加 `tia.cmd`（V21）/ `tia-v20.cmd`（V20），把根目录加进 PATH 即可随处 `tia gen ...`，无需记忆深层 exe 路径。
- **spec 模板开箱即用**：`tia` 自动把 spec 里的 `__BUNDLE__` 解析成交付包根目录（向上探测含 `templates/`+`tools/` 的目录），现成模板无需手动替换路径即可直接 `tia gen`。
- **`.bat` 双版本回退**：`生成工程.bat`/`预热.bat` 在 V21 exe 缺失时自动回退到 V20 exe，V20 用户也能拖拽即用。

## [1.0.0] - 2026-06-02

首个 1.0 大版本，聚焦「快、好用、不出错」。

### 性能 — 启动从分钟级降到秒级

- **默认 headless 启动**：连接 TIA 时默认 `WithoutUserInterface`，冷启动从约 200–340s 降到约 10–28s（实测全量回归 21/21 通过，含 WinCC Unified HMI）。需要可视化检查时加 CLI `--with-ui` 启动完整 GUI。
- **常驻实例（可选）**：附带 `scripts/prewarm_tia.py`，保活一个 headless TIA 后，后续会话的 `Connect` 直接 attach，约 0.8–1s（实测并发 attach 可行）。

### 新工具 — 一次调用生成完整工程

- **`ScaffoldProject`**（L1）：单个 JSON spec 一步生成完整工程——自动连接 → 建项目 → 加 PLC（+可选 Unified HMI）硬件 → UDT/全局 DB/PLC 标签表 → 导入 SCL 外部源与 LAD（S7DCL）→ 编译 → HMI 连接/画面/变量 → 保存，返回逐步报告。把约 20 步的 runbook 收成一次调用。支持 `dryRun=true` 离线校验 spec（块 JSON 形状/SCL·LAD 文件/designJson）不连 TIA。
- **现成 spec 模板**：`templates/project-blueprints/scaffold_spec_start_stop.json`（启停控制）、`scaffold_spec_motor.json`（电机控制），均用已验证构建块拼装、编译 0 错。SKILL.md 新增 §0.5 黄金路径。

### 可靠性

- **HMI 软件路径自动解析**：ScaffoldProject 不再写死 `HMI_RT_1`，按设备命名探测真实运行时路径。
- **连接更稳**：`ConnectPortal` 给 attach 加 30s 上限，挂死/孤儿 TIA 实例从约 200s 卡死改为快速跳过并启动新实例。
- **导入回读校验**：`ImportFromDocuments` 与 `ImportBlock` 导入后回读确认块已存在，返回 `Meta.verified`，便于自我纠错。

### 工具收敛

- 工具数 184 → **180**：下线 4 个 `Export*ToTemp` 便捷变体（改用基础导出工具 + 自选目录）；为易混的 Export/Import 工具补充「何时用本工具 vs 替代」消歧描述（XML ↔ SCL、单个 ↔ 批量 ↔ 整程序）。

## [0.0.40] - 2026-06-02

### 示例库质量 — SCL/UDT/DB 全面补注释并丰富逻辑

- **5 个 `scl-examples/*.scl` 重写**：块头说明 + 每个 `VAR_INPUT/VAR_OUTPUT/VAR` 接口变量逐行中文注释 + 逻辑分区注释；`FB_TimerCounterDemo` 增运行/剩余/完成百分比输出并用静态累计器消除「输出未初始化」告警（编译 0 错 0 警）；`FB_BasicLatch` 增 `Healthy`；`FB_StepSequenceDemo` 增进度百分比；`FC_BasicScaleLimit`/`FC_MathCompareDemo` 增限幅/方向/偏差百分比输出。
- **`udt_basic_status.json` / `db_basic_status.json`** 每个成员补 `commentZhCn` 中文注释（builder 早已支持）。

### 仓库管理 — 编译产物移出 git 跟踪

- `bin/Release`、`bin-v20/Release` 下的 exe/DLL/.config 不再入库（加入 `.gitignore`），二进制改由 GitHub Release zip 分发。消除「clean 误删 tracked 二进制 → MCP 启动崩溃」这一类停摆隐患。

## [0.0.39] - 2026-06-01

### Stability-first public project generation

- `PlcBuildAndImport` response now includes `CapabilityDecision`, `CapabilityWarnings`, and `RecommendedNextActions`. Complex SCL-like expressions are surfaced during dry-run as `external-scl-recommended`, so clients can choose native `.scl/.s7dcl` templates instead of forcing the narrow XML DSL into TIA compile errors.
- `ApplyUnifiedHmiScreenDesignJson` now supports `strict=true` by default. Unsupported property/text writes fail the tool instead of reporting a false successful HMI layout.
- Unified HMI design JSON now has a small stable property guard for generated controls. For example, Rectangle text/foreground/font writes are rejected with guidance to use a separate `HmiText` item; IOField ad-hoc process-value writes are redirected to `BindUnifiedHmiTagDynamization`.
- `EnsureUnifiedHmiTag` now verifies HMI tag binding readback by default. Stable generation requires `SymbolicVerified` or `AbsoluteVerified`; internal-only/unverified tags fail with readback details and guidance. Internal HMI-only validation probes can explicitly set `requireVerifiedBinding=false`.
- Version bumped to `0.0.39` on both V20/V21 builds and the package manifest.

## [0.0.38] - 2026-05-31

### PLC SCL 生成可靠性 — 消除「表达式被当成单变量名」这一类编译故障

- **`StructuredTextXmlBuilder` 加 fail-fast 护栏**：`condition` / `assignment.source` / `line` 的 `{sym}` 经 `LocalVariable` 时校验合法 SCL 标识符，遇到含运算符/空格/括号的表达式（`RawMax <> RawMin`、`Setpoint - Actual`、`ABS(x)`、`Disable OR FaultLatch`）或布尔字面量 `TRUE`/`FALSE` **在离线 `dryRun` 阶段直接抛错**，不再静默生成「变量名含整段表达式」的错误 XML、拖到 TIA 编译期才暴露成 `Tag #"…" not defined`。全局（带引号）符号名不受影响。
- **5 个含表达式/CASE/TON 的 FC/FB 模板改走外部 SCL**：`FC_BasicScaleLimit` / `FC_MathCompareDemo` / `FB_BasicLatch` / `FB_TimerCounterDemo` / `FB_StepSequenceDemo` 从 `plcbuild-json` DSL 改为 `scl-examples/*.scl` 原生源（`ImportPlcExternalSource` + `GenerateBlocksFromExternalSource`）；蓝图 `full_plc_hmi_project.json` 的 `objects`/`templateFiles`/`requiredBundleFiles`/`importOrder` 同步重写；旧 json 保留并标 `_deprecated`。新 `.scl` 文件加 UTF-8 BOM，避免含中文注释时按 GBK 误解码。

### 在线安全红线 — 写强制工具不再 AI 可调用（破坏式）

- **移除 `SetForceTableEntry` 的 MCP 工具暴露**（写强制会覆盖运行中 PLC 逻辑，不应由 AI 调用）；底层 `Portal` 能力保留，供 TIA 人工调试使用。工具数 **184 → 183**（L2 153 → 152）。
- **收紧在线监视安全自检范围**：`RunOnlineMonitoringSafetySelfTest` 的 `safety.no-force-tools` 与对应单测改为允许只读 `Get*`（保留 `GetPlcForceTables` 列举只读），只对「写/执行强制」工具亮红线。消除了「shipped 自检工具报失败」与「force 工具暴露」之间的矛盾。

### 文档/清单一致性

- **修复 `tool-capability-matrix.md` 生成器**：旧产物每行工具名都是未展开的 `$(@{name=…}.name)`（历次发布均损坏）。新增 `scripts/Generate-ToolCapabilityMatrix.ps1` 从 `[McpServerTool]` 静态抽取（支持多行 Description 拼接），重生成为 183 行干净表。
- `basic-plc-template-library.md` / `templates/plc/README.md` / `SKILL.md` §10 / `basic_plc_instruction_recipes.json`：统一「FC/FB 走外部 SCL、DSL 只接受单变量名」的口径，消除与蓝图的矛盾；`instruction-recipes` 修正 2 个指向已弃用 json 的 `plcBuildTemplate` 指针并加防陷阱规则。
- `package-manifest.json` `bundleVersion` 由滞后的 `0.0.36` 修正为 `0.0.38`。
- HMI `overview` 模板表头 `Symbolic`→`Absolute`，与绝对地址绑定策略一致。

### 测试

- 新增 SCL 护栏单测（4 类表达式输入均断言抛错）。
- 修复 6 个过时离线单测：UDT builder 现要求 `$.name`（测试补名）；工具描述标签由旧约定（`[Plc/Build]`/`Hardware/Network`/`HmiUnified`）更新为现行 `[L2][Domain]`。
- 重建 V20/V21 exe（0.0.38）。

### 真机测试修复（生成示例项目时发现，离线校验无法发现）

- `FB_TimerCounterDemo.scl`：`Counter` 是 S7-SCL 保留字，作静态变量名导致外部源生成失败 → 改名 `CountAccum`；S7-1200 多重背景 `TON` 经外部源导入后编译报 IN/PT 形参无效 → 暂移除定时器，`DelayedDone` 以 `Enable` 驱动并加注释。
- `EnsureUnifiedHmiButtonAction` / `EnsureUnifiedHmiButtonEventHandler` 的 `eventType` 参数描述错写 `Pressed`/`Released`/`Click`/`Press`/`Release`（`HmiButtonEventType` 枚举里都不存在，实际为 `None/Activated/Deactivated/Tapped/KeyDown/KeyUp/Down/Up/ContextTapped`）→ 改为正确示例 `Down`/`Up`/`Tapped`，避免按钮动作 SetScriptCode 失败。

### 性能 — 缓存 softwarePath 解析，减少每次 HMI/PLC 调用的 Openness 往返

- `Portal.GetSoftwareContainer` 原先每次调用都遍历整棵设备树（`_project.Devices` + 组），对每个 DeviceItem 调 `GetService<SoftwareContainer>`（PLC_1 一个设备就 ~30 个 item），约 40 次 COM 往返/次；批量建 15 个 HMI 标签/画面/按钮动作时被重复 N 次（实测每次工具调用约 2s，且 TIA Openness 单线程串行）。
- 新增按 `softwarePath` 缓存解析结果，用 `ReferenceEquals(_project)` 自动失效（项目 open/close/create/attach 都会重建 `_project`，零 COM 开销）；加设备只引入新路径=缓存未命中，无 delete-device 工具，故不会过期。解析逻辑不变，仅加快路径返回同一对象。

## [0.0.37] - 2026-05-31

### 错误处理统一（E）— 消除 LastXxxError 侧信道，统一为 PortalException

- 6 个错误域逐域改造（Connect/Hmi/PlcGen/Compile/Import/AddDevice）：`Portal.cs` 方法失败由 `return false/null` + 可变 `LastXxxError` 侧信道字段，统一为 `throw PortalException(code, msg)`；`McpServer.cs` 工具层统一 `catch (PortalException)` → `McpException("...[{Code}]: {msg}")`，结构化错误码进消息。
- 删除 5 个侧信道字段：`LastHmiError`/`LastPlcGenError`/`LastCompileError`/`LastImportError`/`LastAddDeviceError`。`LastConnectError` 故意保留（成功路径向 Bootstrap 提供诊断、且 OpenProject/OpenSession 共用失败分支，非纯错误通道）。
- `throw PortalException` 19→90 处；`catch (PortalException)` 1→28 处。仅改动 `Portal.cs` + `McpServer.cs` 两个文件，净减 ~94 行。
- **批量语义保留并修正**：所有 `*FromDirectory` / `ImportPlcProgramFromDirectory` / 分类导入助手逐项 `try/catch(PortalException)` 收集 `ImportFailure`；顺带修正了原先块/类型导入因已抛异常而"一条失败中断整批"的不一致（现与 tagtable/techobject 一致逐项收集）。
- 行为保留：AddDeviceWithFallback 元组 Error、RepairAndReimportBlock 失败返回诊断不抛、CompileSoftware 返回 CompilerResult（其 State 表达编译结果≠硬失败）。AddDevice/Search 硬失败（关键词空/目录不可用）改抛属轻微改善，no-match 仍返回空列表。
- 重建 V20/V21 exe（0.0.37，0 错误）。

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

