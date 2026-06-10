# TIA Portal MCP V17 分支

[English](README.en.md) · **中文**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

本分支面向 **TIA Portal V17 / Openness V17**。

当前目标是 **PLC-Software Phase 1**：验证 MCP 能在 V17 下完成离线 PLC 工程主链路，包括项目、硬件、PLC 软件对象生成/导入、编译、保存和读回。

这不是 V17 全功能版。当前不覆盖 HMI、PLCSIM 自动下载、在线写值、force 或生产设备下载。

`master` 分支继续保持现有主线；V17 相关 PR 只提交到 `v17` 分支。V17 分支的验证、bugfix 和后续维护由 `mianmianlingqi` 负责。后续如果有通用且稳定的修复，再单独拆成小 PR 回 `master`。

---

## 当前状态

| 项目 | 状态 |
|------|------|
| TIA 版本 | TIA Portal V17 / Openness V17 |
| 分支定位 | V17 专用分支 |
| 当前阶段 | PLC-Software Phase 1 |
| 编译目标 | `tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V17.csproj` |
| 运行时输出 | `tools/tiaportal-mcp/src/TiaMcpServer/bin-v17/Release/net48/TiaMcpServer.exe` |
| 工具 profile | `plc-software-v17-phase1` |
| 维护者 | `mianmianlingqi` |

---

## 支持范围

当前 V17 Phase 1 支持：

- 创建、打开、保存和关闭 TIA 工程
- 搜索硬件目录
- 添加 S7-1200 / PLC 设备
- 构建并导入 PLC tag table
- 构建并导入 UDT
- 构建并导入 global DB
- 导入 PLC XML
- 导入外部 SCL source
- 从外部源生成 PLC blocks
- 编译 PLC 软件并返回结构化诊断
- 保存后关闭重开工程
- 读回 software tree、blocks、types、tag tables
- 对 V17 不支持的能力返回明确 gated message

---

## 不包含内容

当前 V17 Phase 1 不包含：

- HMI / 画面生成
- PLCSIM 自动下载
- 自动下载到 PLC
- 在线写值
- force
- 生产设备下载
- `V20+ Documents / S7DCL` 的真实导入导出
- `.ap17` 工程文件
- `bin` / `obj` / runtime 输出
- 日志、截图、备份
- 本机绝对路径产物
- 临时验证工程

其中 `ExportAsDocuments`、`ImportFromDocuments` 等 Documents/S7DCL 工具在 V17 下只返回明确门禁说明，例如：

```text
requires TIA Portal V20 or newer
```

---

## 环境要求

- Windows
- .NET Framework 4.8
- TIA Portal V17
- TIA Portal Openness V17
- 当前 Windows 用户已加入本地组 `Siemens TIA Openness`
- 首次连接时在 TIA Portal 中确认 Openness 授权弹窗

如果 TIA Portal V17 安装在非默认路径，启动时请显式传入：

```text
--tia-portal-location "<TIA Portal V17 install root>"
```

---

## 构建

在仓库根目录执行：

```powershell
dotnet build .\tools\tiaportal-mcp\src\TiaMcpServer\TiaMcpServer.V17.csproj -c Release
```

构建产物：

```text
tools\tiaportal-mcp\src\TiaMcpServer\bin-v17\Release\net48\TiaMcpServer.exe
```

---

## 启动 MCP

V17 必须显式启用运行时 profile：

```powershell
.\tools\tiaportal-mcp\src\TiaMcpServer\bin-v17\Release\net48\TiaMcpServer.exe `
  --tool-profile plc-software-v17-phase1 `
  --tia-major-version 17 `
  --tia-portal-location "<TIA Portal V17 install root>" `
  --with-ui
```

也可以使用环境变量启用 profile：

```powershell
$env:TIA_MCP_TOOL_PROFILE = "plc-software-v17-phase1"
```

`tools/list` 是运行时工具面的权威来源。当前 V17 profile 已验证工具数为 `62`。

---

## 推荐调用顺序

首次使用建议按以下顺序验证：

```text
Bootstrap
-> DiagnosePortalConnectReadiness
-> Connect
-> CreateProject
-> GetProjectTree
-> SearchHardwareCatalog
-> AddDeviceWithFallback
-> PlcBuildAndImport
-> CompileAndDiagnosePlc
-> SaveProject
-> CloseProject
-> OpenProject
-> GetSoftwareTree
-> GetBlocksWithHierarchy
-> GetTypes
-> GetPlcTagTables
```

`softwarePath` 不要硬猜。请先通过 `GetProjectTree` 或 `GetSoftwareTree` 读回真实 PLC 软件路径，再传给 PLC 相关工具。

---

## Connect 注意事项

V17 Openness 在部分机器上可能出现人工确认阻塞：

- 首次 attach / launch 可能弹出 TIA/Openness 授权或安全确认框
- V17 默认采用 GUI-first 策略，优先附着可见 TIA GUI
- 无可见实例时优先 `WithUserInterface` 启动，便于用户看到确认弹窗
- 可先运行 `DiagnosePortalConnectReadiness` 判断是否疑似卡在确认框
- 若返回 `ConfirmationRequired`，请切到 TIA Portal 界面确认，再重试 `Connect`
- `--without-ui` 仅保留给明确需要 headless 的高级场景，不作为 V17 默认路径

---

## 已验证内容

本分支已在本地 TIA Portal V17 / Openness V17 环境验证：

- `dotnet build TiaMcpServer.V17.csproj -c Release` 通过
- `tools/list` 可读出 `plc-software-v17-phase1` 工具面
- V17 profile 工具数为 `62`
- 必需 PLC 主链路工具均存在
- HMI / download / online write / force 工具未暴露在该 profile 中
- `ExportAsDocuments` / `ImportFromDocuments` 在 V17 下返回明确 gated 提示
- 真实 V17 PLC smoke 通过：

```text
CreateProject
-> AddDeviceWithFallback
-> PlcBuildAndImport
-> CompileAndDiagnosePlc
-> SaveProject
-> CloseProject
-> OpenProject
-> Readback
```

真实 smoke 中已验证对象：

- `TT_PRSmoke`
- `UDT_PRSmokeState`
- `DB_PRSmoke`

编译诊断结果：

```text
errorCount = 0
warningCount = 0
```

---

## 贡献约定

V17 分支接受面向 TIA Portal V17 / Openness V17 的 PR。

请不要提交：

- `.ap17` 工程文件
- `bin` / `obj`
- 运行时输出
- 日志
- 截图
- 备份目录
- 本机绝对路径产物
- 临时验证工程
- token、cookie、证书或其他敏感信息

建议每个 V17 PR 附带：

- build 结果
- `tools/list` 结果摘要
- gated 工具抽查结果
- 如涉及真实 TIA 行为，附 smoke 验证摘要

---

## 相关文档

| 文档 | 用途 |
|------|------|
| `手册/quickstart.md` | 快速启动和 MCP 基础调用方式 |
| `tools/tiaportal-mcp/skill/SKILL.md` | MCP 工具使用规范 |
| `docs/tool-capability-matrix.md` | 静态能力矩阵 |
| `manifest/tools-list.json` | 静态工具清单 |

注意：静态工具清单可能与运行时 profile 不完全一致。V17 下请以启动后的 `tools/list` 为准。
