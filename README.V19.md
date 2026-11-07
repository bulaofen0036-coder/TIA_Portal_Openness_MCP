# TIA Portal V19 引擎（master 2.7.8 全量合并）

> 状态：**V19 构建 0 errors / 2 既有 warnings；离线测试 479 passed；真机验收 48 PASS / 2 FAIL / 1 BLOCKED / 7 N/A**（2026-10-30）。
> 核心链、块读取与导出、HTTP 编码、V19 边界、编译、生成、日常编辑（除改名）、HTTP 安全已真机验证；HMI 未测（测试工程无 HMI）。
> 已知问题：`ExportDeviceAml` 在 V19 上可能让 TIA 实例崩溃；`SetPlcTag` 改名在 V19 不受支持（`PlcTag.Name` 只读）。
>
> **给 AI agent**：先读 [`docs/AGENT_COOKBOOK.md`](docs/AGENT_COOKBOOK.md)（决策表 / 黄金路径 / 参数规则 / token 纪律），
> 经验库在 [`docs/AGENT_LEARNINGS.md`](docs/AGENT_LEARNINGS.md)。
> 全部 **233** 个工具（V19 编译面）的说明与索引：[`docs/TOOLS_REFERENCE.md`](docs/TOOLS_REFERENCE.md)（机器索引用 `docs/tools-index.csv`）。

## 这是什么

上游 `v19` 已合入本项目的 V19 移植。本分支不再逐条回移，而是把 **master 2.7.8 全量合并**进 `v19`，只保留 V19 无法编译的 API 的 guard：

- 编译目标 **Openness 19.0**（`Siemens.Collaboration.Net.TiaPortal.Packages.Openness 19.0.1725520202`）
- 新增 `TiaMcpServer.V19.csproj`，定义 `TIA_V19`，输出 `bin-v19/`
- 默认 TIA 版本 **19**（`Engineering.DefaultTiaMajorVersion`），Openness 初始化门槛 `>= 19`
- V19 缺失的 API 用 `#if !TIA_V19` 排除，运行期由 stub 返回 `NotSupportedOnVersion`
- 全量合并后 **233 个工具**：master 238 个减去 5 个 VCI 工具；4 个文档工具仍在但不可用

## 全量合并结果

| 项目 | 结果 |
|---|---|
| 合并上游 | master `2627ee0`（2.7.8），103 commits / 226 files |
| 合并冲突 | 2 个：`Program.cs`、`Siemens/Portal.cs` |
| V19 适配 | 约 30 行 guard + V19 csproj/stub/compat |
| 构建 | 0 errors，2 warnings（MSB3539、OpcUaLiveReader CS8602，均为既有） |
| 离线测试 | `TiaMcpServer.Tests` 472 passed / 0 failed |
| 工具数 | 238 → 233（5 个 VCI 不编译） |

关键 V19 适配：

- `Program.cs`：默认版本回退 `DefaultTiaMajorVersion`；Openness 初始化门槛 `>= 19`。
- `Siemens/EngineRouter.cs`：`TIA_V19` → `CompiledTiaMajorVersion=19`；sibling 查找认 `bin-v19`。
- `Siemens/Portal.Blocks.cs` / `Portal.Helpers.cs`：SIMATIC SD 文档 API 用 `#if !TIA_V19` 排除。
- `Siemens/Portal.Devices.cs`：V19 复用 V20 的 `CommunicationConnections` 反射降级。
- `Siemens/Portal.Software.cs` / `Portal.HwHmiConnection.cs`：跳过 V21 硬件层 HMI 连接。
- `McpServer.VersionControl.cs`：整个 VCI 文件 `#if !TIA_V19`（V19 没有 `MappedObject`）。

## V19 能力边界

| 能力 | V19 | 说明 |
|---|---|---|
| SIMATIC SD / S7DCL 文档导入导出 | ❌ | 4 个工具仍列出；V19 上返回 `InvalidParams` + V20 提示（见「已知限制」） |
| VCI（版本控制接口） | ❌ | 5 个工具不编译；`MappedObject` 为 V21+ |
| 硬件级 HMI 连接创建 | ⚠️ 降级 | V21 的 `CommunicationConnections` 不存在，走软件层；经典面板可能建不出 |
| 经典 HMI / WinCC Unified | ⬜ | 编译通过；测试工程无 HMI，画面 / 变量 / 连接 / 编译未真机验证 |
| PLC 块 / 变量表 / UDT / DB / 编译 | ✅ | 2.7.8.1 真机验证通过（含 NBSP 组名） |
| Cax/AML 设备导出 | ⚠️ | V19 真机导出会让 TIA 实例崩溃，见「已知限制」 |
| 软件单元读取 | ✅ | 真机调用通过（测试工程无软件单元） |
| 日常编辑 11 工具 | ⚠️ | 除 `SetPlcTag` 改名（V19 只读）外已真机验证 |
| 运行时只读监控（S7 / OPC UA） | ✅ | 不依赖 Openness，独立通道 |

## 默认工具档位

master 2.4+ 默认 **lite**（核心工具）。要看到全部 233 个工具：

```bat
rem MCP 客户端启动参数
--profile full

rem 或环境变量
set TIA_MCP_PROFILE=full
```

lite 档下仍可通过 `FindTools` + `CallTool` 按需调用非 lite 工具。`ScaffoldProject` 和删除类工具默认 `dryRun=true`：先 dry run，确认后再传 `dryRun=false`。

## 构建

### Windows（推荐）

要求：TIA Portal **V19** 已安装、.NET Framework 4.8、.NET SDK（`dotnet` 在 PATH）。

```bat
rem TIA 装在默认位置
build-v19.cmd

rem 非标准安装路径
build-v19.cmd "D:\app\TIA19\Portal V19"
```

产物：`tools\tiaportal-mcp\src\TiaMcpServer\bin-v19\Release\net48\TiaMcpServer.exe`

### Linux / CI（交叉编译）

NuGet 包本身不含 Openness DLL；把 Windows 机器上的 `Portal V19\PublicAPI\V19` 整个目录拷过来：

```bash
./build-v19.sh "/path/to/PublicAPI/V19"
```

> Siemens 的 DLL 有版权，**不要提交进仓库**。

## 运行

```bat
rem 打印版本，不需要 TIA
tia-v19.cmd version
rem 命令用法
tia-v19.cmd help
rem spec 字段说明，离线
tia-v19.cmd schema
rem 离线冒烟：version / self-test / gen --dry-run
smoke-v19.cmd
rem 追加 connect self-test，会启动 TIA
smoke-v19.cmd --with-tia
```

MCP 客户端配置（Cursor / VS Code / Claude Desktop），全量工具：

```json
{
  "mcpServers": {
    "tia-portal-v19": {
      "command": "C:\\path\\to\\TiaMcpServer.exe",
      "args": ["--tia-portal-location", "C:\\Program Files\\Siemens\\Automation\\Portal V19", "--tia-major-version", "19", "--profile", "full"],
      "env": { "TIA_MCP_PROFILE": "full" }
    }
  }
}
```

TIA 装在默认位置且只装 V19 时，`--tia-major-version` 可省略（会自动检测）。

## 真机验证（2026-10-30，2.7.8.1）

工程 `configuar_t20261028`（S7-1500，无 HMI），TIA V19。58 条验收矩阵：
**48 PASS / 2 FAIL / 1 BLOCKED / 7 N/A**；必过组全绿（编码回归 5/5、V19 边界 4/4、删除 dryRun 2/2）。

| # | 检查项 | 结果 |
|---|---|---|
| 1 | `tia-v19 version` → 2.7.8.1；自动检测到 19 | ✅ |
| 2 | `--run-capability-self-test --self-test-connect` → 初始化 + `IsConnected=True` | ✅ |
| 3 | `gen` 最小 spec → 建工程 / 加 PLC / DB / 变量表 | ✅ 5 ok / 0 failed |
| 4 | 编译（软件 / 硬件） | ✅ 0 errors |
| 5 | 保存 | ✅ |
| 6 | `ExportAsDocuments` 等 4 个文档工具 | ✅ 明确拒绝（`InvalidParams` + V20 提示；见已知限制） |
| 7 | `DescribeBlockLogic` 读 SCL / LAD 块 | ✅（含 U+00A0 组名） |
| 8 | `ExportBlock` / `ExportBlocks` 用 `Program blocks/...` 根前缀 | ✅ |
| 9 | 版本路由日志不出现 “rerouting to V21” | ✅ |
| 10 | HTTP 请求体 UTF-8（NBSP / 中文） | ✅ 码点级验证 |
| 11 | 删除类工具 `dryRun=true` 语义 | ✅ |
| 12 | `ExportDeviceAml`（CAx/AML） | ❌ 见已知问题 1 |
| 13 | `SetPlcTag` 改名 | ❌ 见已知问题 2 |
| 14 | HMI（经典 / Unified） | ⬜ N/A：测试工程无 HMI |
| 15 | SCL 源码导入 | ⬜ 未覆盖 |

## 已知限制

- **`ExportDeviceAml` 可能让 TIA 实例崩溃（V19 真机实测）。** `CaxProvider.Export` 抛异常后
  TIA 进程退出，`Portal` 留下 disposed 句柄；随后 `GetState` 会报 `Access to a disposed object`。
  `Connect` + `OpenProject` 可恢复。当前没有故障隔离；在生产工程上调用前先归档，或暂时不用该工具。
- **`SetPlcTag` 改名在 V19 不受支持。** `PlcTag.Name` 是只读属性，`SetAttribute("Name")` 会报
  `'set_Name' is not supported`；请在 TIA 界面改名（代码引用会跟随），或新建变量后删除旧变量
  （代码/HMI 的按名引用不会自动迁移）。
- **文档 API 的错误码与文档不一致。** V19 上 4 个文档工具先命中 `TiaMajorVersion < 20` 前置门，
  返回 `InvalidParams` + `requires TIA Portal V20 or newer`，不是 `NotSupportedOnVersion`；
  拒绝行为正确，错误码待统一。
- **HMI 未验证。** 测试工程没有 HMI；经典 / Unified 的画面、变量、连接、编译仍待有 HMI 的工程。
- VCI 5 个工具在 V19 上不存在；4 个文档工具仍列出但不可用。
- V19 的 Openness 是单体 `Siemens.Engineering.dll`（V21 起拆分）；升级上游时保持 `TIA_V19` guard。
- 默认 lite 档；需要全量工具时显式 `--profile full` / `TIA_MCP_PROFILE=full`。

## 维护策略

- `v19` 跟随 master 合并，不再逐条回移；每次合并后重跑本文件的真机矩阵。
- 连续 6 个月无 V19 issue 且 master 不再兼容 V19 API → 仅安全修复；再 6 个月 → 归档。
