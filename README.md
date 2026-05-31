# TIA Portal MCP 完整交付包（**v0.0.36** / V20+V21 + S7DCL）

[English](README.en.md) · **中文**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Release](https://img.shields.io/github/v/release/bulaofen0036-coder/TIA_Portal_Openness_MCP)](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/releases) [![validate-bundle](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/actions/workflows/validate.yml/badge.svg)](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/actions/workflows/validate.yml)

> **免费开源（MIT）**：服务器**无需任何 license key** 即可运行，**不含任何授权校验代码**。

![架构图](docs/assets/architecture.svg)

在 **Windows + TIA Portal V20 或 V21** 下，通过 **MCP（stdio 或 HTTP）** 驱动博途：建项目、加硬件、生成 PLC（Tag/UDT/DB/SCL/LAD）、生成 **WinCC Unified** 画面与事件、编译诊断、保存。  
包内含 **已编译运行时**、Skill、静态工具清单、能力矩阵、PLC/HMI 模板、**一键可读的项目蓝图**与手册。**不要求**另行克隆源码仓库。

**本次更新（相对 20260512 包）**

- **双版本支持（V20 + V21）**：包内含两个 exe — `bin/Release/net48/TiaMcpServer.exe`（V21 编译）与 `bin-v20/Release/net48/TiaMcpServer.exe`（V20 编译）。
  - 二者**必须分别使用**，不能互换：V21 用 split DLL（`Siemens.Engineering.Base/Step7/...`），V20 用单体 `Siemens.Engineering.dll`，IL 层面绑定不同。
  - 两份 exe 都接受新 CLI 参数 `--tia-portal-location <path>`，配合 `--tia-major-version <20|21>` 用于非标准安装位置。
- **S7DCL/SCL 文本格式工具**：`ExportAsDocuments` / `ExportBlocksAsDocuments` / `ImportFromDocuments` / `ImportBlocksFromDocuments` 在 V20+ 项目里以 SIMATIC SD 文本格式（`.s7dcl + .s7res`）导入导出程序块，比 SimaticML XML 更易读、diff 友好；描述里标注「PREFERRED on V21+」引导 AI 优先选用。
- **V21 端到端验证**（DemoProjects/MCP_Demo_Rich_20260523）：8 块导出 + 8 块导入回环 14.7s。
- **V20 端到端验证**（江夏测试5T车_V20）：CompileSoftware → ExportBlocksAsDocuments，51 个 `.s7dcl` + 33 个 `.s7res` 全量导出成功。LAD 块以 `RUNG / I_Contact / Coil / TON{...}` 文本表达，diff 友好。

**与 IDE 无关**：凡支持 MCP 的客户端（Cursor、VS Code、Claude Desktop、自研 HTTP 客户端等）均可使用同一 `TiaMcpServer.exe`。若某 IDE 中「看不到某个工具」，属于 **客户端工具描述符/缓存** 问题，不是交付包裁剪能力；见 `docs/mcp-ide-and-tool-visibility.md`。

**与源码仓库无关**：接收方只需解压本包；在 MCP 配置里把 `command` 指到本包内的 `tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe`（见 `cursor-mcp.example.json`，将 `REPLACE_ME` 换成本包根目录）。其它文档若出现 `…\PID博途块\…` 等开发机路径，仅为作者构建位置，**不要求**克隆该仓库。

---

## 上手步骤

1. **环境准备**  
   - .NET Framework **4.8**、**TIA Portal V20 或 V21** 已安装；  
   - 当前用户加入 **`Siemens TIA Openness`** 本地组，注销重登；  
   - 三选一定位博途安装根：  
     a) 启动 exe 时传 `--tia-portal-location "D:\app\TIA20\Portal V20"`（推荐，非标准安装位置必用）；  
     b) 用户环境变量 `TiaPortalLocation` 指向博途安装根；  
     c) 让程序自动从注册表 `HKLM\SOFTWARE\Siemens\Automation\_InstalledSW\TIAP{20|21}\TIA_Opns\Path` 读取。  
   - 当机器装了多个版本时显式传 `--tia-major-version 20`（或 21）以免自动选最高版；  
   - 首次连接时在 TIA 弹窗中授权 **Openness**。

2. **挂载 MCP**  
   - 复制 `cursor-mcp.example.json` 片段到任意支持 MCP 的客户端（Cursor / VS Code / Claude Desktop / 自研 HTTP 客户端均可）；  
   - 将路径中的 **`REPLACE_ME`** 替换为 **本包根目录**；
   - **按 TIA 版本选 exe 路径**：  
     - V21 → `…\tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe`  
     - V20 → `…\tools\tiaportal-mcp\src\TiaMcpServer\bin-v20\Release\net48\TiaMcpServer.exe`  
   - 非标准安装位置必须在客户端 `args` 里加 `--tia-portal-location "<安装根>" --tia-major-version <20|21>`，例如 `"--tia-portal-location","D:\\app\\TIA20\\Portal V20","--tia-major-version","20"`。

3. **首次调用顺序**  
   - `Bootstrap` → `Connect` → `OpenProject`（或 `CreateProject`）→ `GetProjectTree`，从树中读取真实的 `PLC_xxx` / `HMI_RT_xxx` 路径再继续。

---

## 脱机校验（不启动博途）

在项目根执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate-Bundle.ps1
```

校验：运行时存在、蓝图列出的文件齐全、`manifest/tools-list.json` 与清单工具数一致、各 PLC/HMI JSON 可解析。  
加 `-Strict` 时对清单与矩阵有更严比对（可选）。

---

## 两条工作路径

| 目标 | 读什么 | MCP 顺序概要 |
|------|--------|----------------|
| **从零生成 PLC + Unified HMI 全套** | `templates/project-blueprints/full_plc_hmi_project.json` + `docs/full-project-generation-runbook.md` | `Bootstrap` → `CreateProject` → 硬件与网络 → **PLC：`PlcBuildAndImport` 每项先 `dryRun=true`** → `CompileAndDiagnosePlc` → **HMI：`EnsureUnified*` + `ApplyUnifiedHmiScreenDesignJson` + `BindUnifiedHmiTagDynamization` + `EnsureUnifiedHmiButtonAction`** → `SaveProject` |
| **只验证 MCP/LAD/SCL 导入链路** | `templates/mcp-full-e2e-verify/README.md` | 在已有工程中按说明导入块与标签，再编译 |

---

## 文档地图

| 路径 | 说明 |
|------|------|
| `tools/tiaportal-mcp/skill/SKILL.md` | **主规范**：工具分层、参数陷阱、Unified HMI schema、LAD/SCL 边界 |
| `manifest/tools-list.json` | 静态工具名与层级；**运行时权威列表**以连上服务器后的 `tools/list` 为准 |
| `docs/tool-capability-matrix.md` | 能力矩阵（静态索引） |
| `docs/full-project-generation-runbook.md` | 完整项目生成步骤 |
| `docs/basic-plc-template-library.md` | PLC 指令与模板说明 |
| `docs/scl-instruction-library.md` | **SCL 指令库**（控制流、缩放、定时计数、PID、斜坡、UDT 等中性模板） |
| `docs/lad-instruction-library.md` | **LAD 指令库**（触点/线圈/比较/算术/定时/计数与 XML 注意点） |
| `docs/hmi-plc-tag-binding-and-addressing.md` | **HMI↔PLC**：默认绝对地址、`DB200` 字节排布、红字排障 |
| `docs/hmi-connection-driver-matrix.md` | **通讯驱动选择**（按 CPU 系列匹配 CommunicationDriver） |
| `docs/mcp-ide-and-tool-visibility.md` | IDE 无关与工具列表权威来源（`tools/list`） |
| `docs/optional-reference-materials.md` | 与仓库 `reference` 目录配合的样板工程说明 |
| `docs/plc-network-patterns-expanded.md` | PLC 网络/指令扩展模式（加长程序段的写法） |
| `docs/tools/*.md` | 分主题：PLC 构建、硬件、HMI 动作等 |
| `手册/quickstart.md` | 英语速启 + 与本 README 对照 |
| `手册/openness-limitations.md` | Openness **不能做** 的事项 |
| `手册/error-model.md` | 错误形态说明 |
| `手册/TIA_NL_INTENT_RECIPES.md` | 自然语言 → 工具序列索引 |
| `templates/plc/README.md` / `templates/hmi/README.md` | 模板索引 |

---

## 标准闭环（缩写）

```text
Bootstrap → Connect → CreateProject → AddDeviceWithFallback → AddHardwareCatalogDeviceWithProbe
→ ConnectDeviceNodesToProfinetSubnet → GetProjectTree → ValidateAutomationContext
→ PlcBuildAndImport(dryRun=true 逐项) → PlcBuildAndImport(dryRun=false 按导入顺序)
→ CompileAndDiagnosePlc → EnsureUnifiedHmiConnection → EnsureUnifiedHmiTagTable → EnsureUnifiedHmiTag
→ EnsureUnifiedHmiScreen → ApplyUnifiedHmiScreenDesignJson → BindUnifiedHmiTagDynamization
→ EnsureUnifiedHmiButtonAction → SaveProject → Disconnect
```

---

## 能力范围与边界

**可做**：项目与硬件、PROFINET、PLC 声明式导入、LAD XML 导入、Unified HMI 连接/变量（默认绝对地址）/画面/按钮 Down·Up/动态化、编译诊断、保存。  

**包内不含**：西门子安装介质、现场导出工程、业务专用工艺。`reference/` 仅作为风格与指令参考，不参与自动化生成；详见 `manifest/package-manifest.json` 中 `notBundled`。

## HMI 绑定策略

- **统一采用绝对地址**：HMI 接口 DB `DB_HMI_Interface` 使用 **非优化（Standard）** 访问，字节偏移见 `templates/plc/plcbuild-json/db_hmi_interface.json` 的 `absoluteLayout`。  
- **变量调用必须传地址**：调用 `EnsureUnifiedHmiTag` 时，按蓝图 `tags[]` 同时传 `plcTag` 和 `address`。例如 `plcTag="DB_HMI_Interface.CmdEnable"`、`address="%DB200.DBX0.0"`；读回时应看到 `Connection=HMI_Connection_1` 且 `Address/LogicalAddress` 为 `%DB200...`。  
- **通讯驱动按实际 PLC 设备选**：`EnsureUnifiedHmiConnection` 的 `plcName` 使用 `GetProjectTree` 的 PLC 软件节点；工具会解析实际 PLC 设备、站点、PN 节点和 CPU 系列，写入 Partner/Station/Node 与对应驱动。详见 `docs/hmi-connection-driver-matrix.md`。  
- **导入顺序**：先 PLC 编译通过 → 建 HMI 连接 → 建变量表 → 建画面 → `BindUnifiedHmiTagDynamization` → `EnsureUnifiedHmiButtonAction`。

---

## 内容索引（路径）

| 路径 | 说明 |
|------|------|
| `tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48/` | `TiaMcpServer.exe` 与依赖 |
| `scripts/Validate-Bundle.ps1` | 交付包完整性校验 |
| `templates/project-blueprints/full_plc_hmi_project.json` | 完整项目蓝图 |
| `templates/plc/` | Tag、UDT、DB、FC、FB、LAD 配方、SCL 示例 |
| `templates/hmi/` | Unified 多页 `designJson` |
| `templates/mcp-full-e2e-verify/` | E2E 验证用导入素材 |
