# TIA Portal MCP 完整交付包（V20+V21 + S7DCL + CLI + 在线只读监控 + 一键配置 + Doctor 体检）

> 当前版本见上方 Release 徽章与 [CHANGELOG.md](CHANGELOG.md)（README 不再硬编码版本号）。

[English](README.md) · **中文**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Release](https://img.shields.io/github/v/release/bulaofen0036-coder/TIA_Portal_Openness_MCP)](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/releases) [![validate-bundle](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/actions/workflows/validate.yml/badge.svg)](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/actions/workflows/validate.yml)

> **免费开源（MIT）**：服务器**无需任何 license key** 即可运行，**不含任何授权校验代码**。

> 🖥️ **不想自己配 MCP 客户端？** 试试 **[TIA 助手](https://aeenhance.com/)** —— 基于本引擎的桌面版：解压即用，内置中文 AI 助手，每次写入工程都先弹窗等你批准。
> 👉 [官网 aeenhance.com](https://aeenhance.com/) · [看 1 分钟实录](https://aeenhance.com/#demo) · 申请试用：**bulaofen0036@gmail.com** · [详细对比 ↓](#tia-助手本引擎的桌面版)

![架构图](docs/assets/architecture.svg)

在 **Windows + TIA Portal V20 或 V21** 下，通过 **MCP（stdio 或 HTTP）** 驱动博途：建项目、加硬件、生成 PLC（Tag/UDT/DB/SCL/LAD）、生成 **WinCC Unified** 画面与事件、编译诊断、保存。  
包内含 **已编译运行时**、Skill、静态工具清单、能力矩阵、PLC/HMI 模板、**一键可读的项目蓝图**与手册。**不要求**另行克隆源码仓库。


## 🆕 v2.5.0：把博途工程放进 Git（版本控制接口 VCI）

博途工程是二进制的，Git 没法 diff —— 于是"版本管理"长期只能靠另存一堆日期文件夹。
V21 的**版本控制接口**把工程映射成一个普通文件夹，**每个块一份文本文件**，可 diff、可 commit、可 review。
本版把整圈做成了几条命令，**全程不用在博途界面里点任何东西**。

对 AI 说一句就够了：

```
把当前工程放进 Git，工作区用 D:\repos\my-plc
```

它会建工作区 → **整工程自动纳管**（几百个块一条命令，不用手工勾选）→ 导出文本，
然后你 `git commit` 即可。之后想知道改了什么：

```
GetVersionControlStatus(changedOnly=true)
→ A3_4_Hoist | Unequal        ← 精确到块
```

- **覆盖**：FC / FB / OB / DB、PLC 变量表、UDT（整个程序侧）。硬件组态和专有技术保护块不在 VCI 覆盖范围内，工具会明确报出来而不是静默跳过。
- **改完要编译才导得出**（博途的限制）；检测不受影响，未存盘也能检测到。
- 配套 `tools/vci-watch/`：**改完编译后自动导出 + 写 CHANGELOG + git commit**，工程师什么都不用做。

📖 完整用法与三个必知行为 → **[docs/version-control-git.md](docs/version-control-git.md)**


## ⚡ 最快上手（3 步，零编程·CLI 路线）

> 第一次用？**不需要 MCP 客户端、不需要写代码。** 装好 TIA 后照这 3 步，几分钟内生成第一个工程。
> （想接 Cursor / Claude Desktop 等 AI 客户端走 MCP？跳到下方 [上手步骤](#上手步骤)。）

1. **准备**：装好 **TIA Portal V20 或 V21** + **.NET Framework 4.8**；把当前 Windows 用户加入本地组 **`Siemens TIA Openness`**，**注销重登一次**（不重登组不生效，这是最常见卡点）。**装的是哪个版本就用哪个**——交付包根目录已备好 `tia.cmd`（V21）/ `tia-v20.cmd`（V20），其余路径自动选。
   - **装完先体检**：跑一次 `tia.cmd doctor`（V20 用 `tia-v20.cmd doctor`）——一次检查 TIA 安装 / exe 版本匹配 / Openness 用户组 / 宿主注册，每项给修法；加 `--fix` 自动补用户组。先体检再干活，能省掉后面 90% 的排障。
2. **预热（可选但强烈推荐）**：双击 `scripts\预热.bat`，留着这个窗口。它常驻一个无界面 TIA，让之后每条命令 **~1 秒**连上（不预热则每次冷启动约 3 分钟）。用完按 `Ctrl+C` 关闭。
3. **生成工程**：把现成模板 `templates\project-blueprints\scaffold_spec_motor.json`（或 `scaffold_spec_start_stop.json`）**拖到 `scripts\生成工程.bat` 图标上**——一条龙建项目→加 PLC/HMI→写块→编译→存盘。退出码 `0` 即成功。
   - 想改成自己的需求：让任意 AI 照 [`docs/AI_spec_prompt.md`](docs/AI_spec_prompt.md) 产出一份 spec（YAML/JSON 都行），再拖给 `生成工程.bat`。
   - 命令行等价写法：把根目录加进 PATH 后，`tia gen <spec>`（先 `--dry-run` 离线校验更稳）。

---

## TIA 助手：本引擎的桌面版

[![TIA 助手 —— 跟它说中文，它去操作博途](https://aeenhance.com/img/og-cover.jpg)](https://aeenhance.com/)

本仓库是**引擎**：你需要自己准备 Cursor / Claude / VS Code 这类 AI 客户端，再把它挂上去。
如果你只想**打开就用**，或者要给不熟悉 MCP 的同事、现场调试人员用，可以看看作者做的 **[TIA 助手](https://aeenhance.com/)** ——
同一台引擎，外面套了一个 Windows 桌面程序，AI 助手、在线监控、曲线记录都做进了界面里。

![TIA 助手实录：写 SCL 块时，删除类操作单独弹窗确认](https://aeenhance.com/video/demo-write.jpg)

<sub>↑ 软件实录截图（演示工程）：让它给两台水泵写一用一备轮换 FB，第一次编译有 4 条警告，它自己改到 0 错误 0 警告；每一步写操作都停下来等人点「是」。[看完整录屏 →](https://aeenhance.com/#demo)</sub>

| | 开源 MCP（本仓库） | TIA 助手（桌面版） |
|---|---|---|
| 形态 | MCP 服务 + `tia` 命令行，接你自己的 AI 客户端 | Windows 桌面软件，**免安装、解压即用**，V20 / V21 双引擎随包自动选 |
| AI | 自备 Cursor / Claude Desktop / VS Code 等 | **内置中文 AI 助手**；DeepSeek、千问、Kimi、智谱、OpenAI 兼容接口任选，接本机 **Ollama 可全程离线**、工程数据不出厂 |
| 写操作把关 | 取决于你用的客户端 | **每次写入都弹窗等你批准**，不可逆操作（删块等）单独确认，审计日志带哈希链 |
| 在线变量 | 只读（S7 绝对地址 / OPC UA，见 [在线实时读值指南](docs/在线实时读值_使用指南.md)） | **不装博途**，S7CommPlus 直连 CPU 按符号读写；写入默认关，开启后强制回读 |
| 曲线记录 | — | 录制、叠加/拆分、导出与回放 CSV |
| 环境体检 | `tia.cmd doctor` 命令行 | 图形化逐项检查运行库 / Openness 白名单 / 用户组，缺什么直接给修法，一键导出诊断包 |
| 许可 | MIT，免费 | 商业授权，**可申请免费试用** |

**怎么试**：发邮件到 **bulaofen0036@gmail.com**，写上你的博途版本（V20 / V21）和想试的场景 → 拿试用包，解压运行，把机器码发回来换授权 Key。
最稳的试法：先让它**只读**把你手头的工程讲一遍，看说得对不对，满意了再让它动手。
更多：[AI 助手](https://aeenhance.com/assistant) · [在线调试](https://aeenhance.com/online) · [安全边界](https://aeenhance.com/security) · [使用手册](https://aeenhance.com/manual) · [开始使用](https://aeenhance.com/start)

> 两边用的是同一台引擎：这里修掉的 bug，TIA 助手里也会修；在 Issues 里提的问题，对两边都有用。

---

## v2.0.0 新功能 —— `tia` 命令行（门槛最低、任意 AI 可用）

> **同一个 exe 既是 MCP 服务，也是命令行。** 任意 AI 产出一份 YAML/JSON spec，任意工程师跑一条命令即可从零建/改工程——**不需要 MCP 客户端、不需要安装**。底层完全复用现有引擎。详见 [`docs/CLI_quickstart.md`](docs/CLI_quickstart.md)。

- **`tia gen <spec.yaml|json>`**：一条命令从 spec 建完整工程（= `ScaffoldProject`）。`--dry-run` 离线校验、`--json` 机器可读。
- **`tia patch <spec>`**：把 spec **增量 upsert 进已有工程**（spec 内 `projectPath` 指向 `.apXX`），未提及的元素不动；`--no-overwrite` 保护手改的 LAD 代码块。
- 还有 `tia compile / describe / export / import / prewarm / schema / version`。退出码 **0=成功 / 1=有失败步骤 / 2=错误**。
- **`tia` 命令入口**：交付包根目录的 `tia.cmd`（V21）/ `tia-v20.cmd`（V20）——把根目录加进 PATH 即可随处 `tia gen ...`，不必记忆深层 exe 路径。
- **零编程上手**：把 spec 拖到 `scripts\生成工程.bat` 上即可（V21 缺失自动回退 V20）；`scripts\预热.bat` 常驻 headless 实例让后续命令 ~1s 连上。
- **现成模板开箱即用**：`templates/project-blueprints/` 的启停/电机 spec 直接 `tia gen` 即可，`tia` 自动解析其中 `__BUNDLE__` 为交付包根目录，无需手改路径。
- **让任意 AI 生成 spec**：见 [`docs/AI_spec_prompt.md`](docs/AI_spec_prompt.md) —— 通用契约「产出一份 spec」，不要求 AI 支持 MCP。
- **YAML + JSON 双解析**：JSON 首选（零歧义），YAML 便于人读写；同一 spec 两者产出一致。

> 仍是双 V20/V21 binary、仍是完整 MCP 服务（`tia` verb 之外行为不变）。CLI 与 MCP 共享同一引擎。

## v1.0.0 新功能（快、好用、不出错）

- **默认 headless 启动，连接快 ~10×**：连 TIA 默认无界面（`WithoutUserInterface`），冷启动从约 200–340s 降到约 10–28s。要肉眼看博途时，启动 exe 加 `--with-ui`（或生成完直接打开 `.ap21`）。
- **`ScaffoldProject` —— 一句话生成完整工程**：传一个 JSON `spec`，一次调用完成「建项目 → 加 PLC/HMI 硬件 → UDT/DB/标签表 → SCL/LAD 块 → 编译 → HMI 连接/画面/变量 → 保存」，返回逐步报告。把约 20 步的 runbook 收成一次调用。`dryRun=true` 可离线校验 spec 再真跑。现成模板见 `templates/project-blueprints/scaffold_spec_start_stop.json`（启停）、`scaffold_spec_motor.json`（电机）。
- **常驻实例，会话秒连（可选）**：开一个终端跑 `python scripts/prewarm_tia.py` 挂着，之后每个会话 `Connect` 约 0.8–1s。
- **更不易出错**：HMI 软件路径自动探测（不再写死 `HMI_RT_1`）；连接对挂死/孤儿 TIA 实例加超时跳过；单块导入（`ImportFromDocuments`/`ImportBlock`）导入后回读确认并返回 `Meta.verified`。
- 工具收敛至 **180**（下线 4 个 `Export*ToTemp` 变体，并为易混的 Export/Import 工具补消歧描述）。

**本次更新（相对 20260512 包）**

- **稳定生成硬门槛（v0.0.39）**：基于 v0.0.38，`PlcBuildAndImport` 会返回 `CapabilityDecision` / `CapabilityWarnings` / `RecommendedNextActions`；`ApplyUnifiedHmiScreenDesignJson(strict=true)` 默认遇到 HMI 属性写入失败即报错；`EnsureUnifiedHmiTag(requireVerifiedBinding=true)` 默认要求读回 `SymbolicVerified` 或 `AbsoluteVerified`，避免“生成成功但变量未真实链接”的公开版体验问题。
- **双版本支持（V20 + V21）**：包内含两个 exe — `bin/Release/net48/TiaMcpServer.exe`（V21 编译）与 `bin-v20/Release/net48/TiaMcpServer.exe`（V20 编译）。
  - 二者**必须分别使用**，不能互换：V21 用 split DLL（`Siemens.Engineering.Base/Step7/...`），V20 用单体 `Siemens.Engineering.dll`，IL 层面绑定不同。
  - 两份 exe 都接受新 CLI 参数 `--tia-portal-location <path>`，配合 `--tia-major-version <20|21>` 用于非标准安装位置。
- **S7DCL/SCL 文本格式工具**：`ExportAsDocuments` / `ExportBlocksAsDocuments` / `ImportFromDocuments` / `ImportBlocksFromDocuments` 在 V20+ 项目里以 SIMATIC SD 文本格式（`.s7dcl + .s7res`）导入导出程序块，比 SimaticML XML 更易读、diff 友好；描述里标注「PREFERRED on V21+」引导 AI 优先选用。
- **V21 端到端验证**（DemoProjects/MCP_Demo_Rich_20260523）：8 块导出 + 8 块导入回环 14.7s。
- **V20 端到端验证**（江夏测试5T车_V20）：CompileSoftware → ExportBlocksAsDocuments，51 个 `.s7dcl` + 33 个 `.s7res` 全量导出成功。LAD 块以 `RUNG / I_Contact / Coil / TON{...}` 文本表达，diff 友好。

**与 IDE 无关**：凡支持 MCP 的客户端（Cursor、VS Code、Claude Desktop、自研 HTTP 客户端等）均可使用同一 `TiaMcpServer.exe`。若某 IDE 中「看不到某个工具」，属于 **客户端工具描述符/缓存** 问题，不是交付包裁剪能力；见 `docs/mcp-ide-and-tool-visibility.md`。

**两种获取方式，引擎 exe 位置不同（脚本已自动兼容两种布局）**：

| 获取方式 | V21 引擎 exe | V20 引擎 exe |
|---|---|---|
| **Release 交付 zip**（推荐） | `tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe` | `tools\...\bin-v20\Release\net48\TiaMcpServer.exe` |
| **git clone 本仓库** | `runtime\v21\TiaMcpServer.exe` | 不随仓库分发——请下载 Release zip |

根目录的 `配置MCP.bat` / `tia.cmd` / `scripts\*.bat` 会按上表顺序自动找 exe，**无需关心布局**；手动配置参考 `cursor-mcp.example.json`（把 `REPLACE_ME` 换成你的实际目录）。其它文档若出现 `…\PID博途块\…` 等开发机路径，仅为作者构建位置，**不要求**克隆源码仓库。

---

## 多版本分支与社区贡献

`master` 始终是 **TIA Portal V20 / V21** 稳定主线，默认行为不随旧版本改动。对更早的 TIA Portal / Openness 版本，我们按版本开设独立分支，欢迎社区在对应分支上贡献与维护：

| 分支 | 目标版本 | 维护方式 |
|------|----------|----------|
| `master` | TIA Portal V20 / V21 | 官方主线（日常改动都进这里） |
| `v21` | TIA Portal V21 / Openness V21 | 官方；从 master 派生，只承接 V21 专属适配 |
| `v20` | TIA Portal V20 / Openness V20 | 官方；从 master 派生，只承接 V20 专属适配 |
| `v19` | TIA Portal V19 / Openness V19 | 社区贡献 |
| `v18` | TIA Portal V18 / Openness V18 | 社区贡献 |
| `v17` | TIA Portal V17 / Openness V17 | 社区贡献 |
| `v16` | TIA Portal V16 / Openness V16 | 社区贡献 |

**怎么贡献**

- 针对某个旧版本的适配 / 修复，请把 PR 提到**对应的版本分支**（如 V17 的改动 → `v17`），不要直接进 `master`。
- `v21` / `v20` 是官方版本线：**同时适用于两个版本的改动仍然进 `master`**；只有当某个修复会改变另一个版本的行为、或依赖该版本独有的 Openness API 时，才提到对应的版本分支。这样双版本共用的代码只维护一份。
- 旧版本的 Openness API 与块 XML 差异较大，分支化可以让各版本独立验证、互不影响主线用户。
- 若某项修复足够通用且稳定，欢迎再单独拆一个小 PR 回 `master`。
- 提 PR 时请**不要提交**：TIA 工程文件（`.apXX`）、`bin` / `obj`、日志、截图、备份、本机绝对路径产物、临时验证工程。

> 想认领某个版本的维护？在 Issues 里说一声即可。

完整协作流程见 [CONTRIBUTING.md](CONTRIBUTING.md)；安全问题报告见 [SECURITY.md](SECURITY.md)。

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

2. **挂载 MCP（一条命令，全自动）**  
   **双击根目录的 `配置MCP.bat`**（V20 用 `配置MCP-v20.bat`）即可；命令行等价写法：`tia.cmd config`。

   它会**自动发现一切**：自己的绝对路径、注册表里的博途安装与版本、与版本匹配的 exe（V20/V21 自动选对），然后把 `tia-portal` 条目一次性写进本机检测到的所有 AI 客户端配置——**Claude Desktop / Claude Code / Cursor / VS Code**（原配置自动备份 `.bak`，其它 server 原样保留）。重启 AI 客户端即生效。  
   - 只配某一个宿主：`config --host vscode`（可选 `claude|claude-code|cursor|vscode`）；  
   - 只看不写（手动粘贴其它宿主）：`config --print`；  
   - **工具档位**：默认就是精简档（62 个核心工具），无需任何参数，见下文《工具档位》；想一次列全 222 个用 `config --full`；  
   - **连不上 / 报错**：`tia.cmd doctor` 一键体检（TIA 安装 / exe 版本匹配 / Openness 用户组 / 宿主注册状态，每项给修法；`--fix` 自动补用户组，v2.2.8）；  
   - **拿错 exe 也没关系**：v2.2.7 起 exe 会按实际 TIA 版本**自动转投**正确的兄弟 exe（前提是兄弟 exe 就在包里：Release zip 同时带 V20 与 V21；git 克隆和插件只带 `runtime\v21`，纯 V20 机器请用 Release zip）。  
   - 手动配置兜底：复制 `cursor-mcp.example.json` 片段，把 `REPLACE_ME` 换成本包根目录；exe 路径按上文「两种获取方式」表选（zip 用 `tools\...\bin[-v20]\Release\net48`，git clone 用 `runtime\v21`）；非标准安装位置在 `args` 加 `--tia-portal-location "<安装根>" --tia-major-version <20|21>`。

3. **首次调用顺序**  
   - `Bootstrap` → `Connect` → `OpenProject`（或 `CreateProject`）→ `GetProjectTree`，从树中读取真实的 `PLC_xxx` / `HMI_RT_xxx` 路径再继续。

---

## 工具档位：精简（默认）与完整

服务端共 **222** 个工具，但默认**只在 `tools/list` 里列出 62 个**。这不是裁能力，是裁上下文——两个原因都是硬的：

- **成本**：222 个工具的 `tools/list` 约 **196 KB（按 4 字符/token 估约 49,000 tokens）**，宿主每一轮对话都要把它重发给模型。精简档约 **57 KB（约 14,000 tokens）**，每轮省掉约七成，模型也不必在 200 多个名字里挑。
- **兼容**：VS Code / GitHub Copilot 的 agent 模式**超过 128 个工具直接报错不干活**，Windsurf 上限 100。全量档在这两个宿主上根本加载不起来。

**能力一个不少。** 没列出来的 160 个工具随用随取：

```
FindTools("watch table")                     → 列出匹配的工具名、参数签名、完整说明
CallTool("ExportPlcWatchTable", "{...}")     → 照常执行，跟直接调用完全一样
```

服务端在握手指令和 `Bootstrap` 里都会告诉模型这件事，所以**不用你交代，AI 自己会用**。

| 档位 | 怎么开 | 列出工具 | 每轮 schema 开销 |
|---|---|---|---|
| 精简（默认） | 什么都不用做 | 62 | ~14,000 tokens |
| 完整 | `config --full`，或 `TiaMcpServer.exe --profile full`，或环境变量 `TIA_MCP_PROFILE=full` | 222 | ~49,000 tokens |

完整档只在「宿主不限工具数、且你就是想让模型直接看到全部」时才有意义；VS Code/Copilot、Windsurf 上不要用。

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

> **新手只需要按顺序看两处：本 README（上手）→ [`docs/README.md`](docs/README.md)（全部文档的导航与阅读顺序）。** 下表是完整清单，供检索。

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
| `tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48/` | `TiaMcpServer.exe` 与依赖（Release zip 布局；V20 在 `bin-v20/...`） |
| `runtime/v21/` | `TiaMcpServer.exe` 与依赖（git clone 布局） |
| `scripts/Validate-Bundle.ps1` | 交付包完整性校验 |
| `templates/project-blueprints/full_plc_hmi_project.json` | 完整项目蓝图 |
| `templates/plc/` | Tag、UDT、DB、FC、FB、LAD 配方、SCL 示例 |
| `templates/hmi/` | Unified 多页 `designJson` |
| `templates/mcp-full-e2e-verify/` | E2E 验证用导入素材 |

---

## 联系作者

- **Bug / 功能建议**：优先提 [Issues](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/issues)，别人撞到同样的问题也能搜到。
- **试用 TIA 助手、定制开发、项目合作**：**bulaofen0036@gmail.com**
- **内测微信群**：群码七天一换，发邮件索取最新的。
- 桌面版官网：**[aeenhance.com](https://aeenhance.com/)**

> 西门子、TIA Portal、SIMATIC、STEP 7、WinCC 为 Siemens AG 的商标。本项目与 TIA 助手均为独立开发的第三方工具，与 Siemens AG 无任何隶属、代理或授权关系。
