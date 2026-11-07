# HMI 连接驱动选择表

## 0. 先决：经典屏 vs Unified 屏（能力边界，选屏前必读）

**本套 MCP 的 HMI 全自动（连接 + 变量绑定 + 画面）只对 WinCC Unified 屏成立。**

| 屏类型 | 连接创建 | 变量绑定 | 画面生成 | 结论 |
|---|---|---|---|---|
| **WinCC Unified**（如 MTP700 Unified Basic `6AV2 123-3GB32-0AW0`） | ✅ `EnsureUnifiedHmiConnection` | ✅ `EnsureUnifiedHmiTag`（V21 集成连接上可按 PLC 符号绑定；V20 用绝对地址） | ✅ `ApplyUnifiedHmiScreenDesignJson` | **可端到端全自动** |
| **经典 / 精简 / 舒适屏**（KTP Basic、TP/KTP Comfort） | V21 ✅ `EnsureUnifiedHmiConnection`（硬件层接口，两端须在同一子网）；V20 ❌ 只能博途界面手建 | ⚠️ XML 导入只认绝对地址（符号 `ControllerTag` 被 Openness 拒） | ❌ 画面 XML 导入极脆（`IOField.set_Visible`、`PropertyComposition.Create` 等属性被拒） | **无法全自动**，只能 GUI 手建连接 / 导模板 |

> **给调用方模型的硬规矩**：若用户没有硬性指定经典屏，且需要"自动建好 HMI"，**在 `AddDevice` 阶段就选 Unified 屏**，经典屏的变量只能按绝对地址导入、起始画面要在博途界面里设，全自动程度低于 Unified 屏。
> 若现场必须用经典屏：TIA V21 上先把两台设备连到同一子网，再用 `EnsureUnifiedHmiConnection` 建连接；TIA V20 上连接需在 TIA 网络视图手动拉一条（约 15 秒）；变量可用**绝对地址** XML 导入（见 `hmi-plc-tag-binding-and-addressing.md`）。

---

本文件给出 `EnsureUnifiedHmiConnection` 与 TIA 内创建 HMI 连接时，**通讯驱动（CommunicationDriver）** 的取值规则。驱动错配会导致 HMI 变量整列红字、运行时无值或连接处显示「未连接」。

## 选择规则

| PLC CPU 系列 | 典型订货号前缀 | CommunicationDriver（包含子串） |
|--------------|------------------|----------------------------------|
| S7-1500 | `6ES7 5xx-…` | `SIMATIC S7 1500` |
| S7-1200 | `6ES7 21x-…` | `SIMATIC S7 1200` |
| S7-300 | `6ES7 31x-…` | `SIMATIC S7 300/400` |
| S7-400 | `6ES7 41x-…` | `SIMATIC S7 300/400` |
| SIMATIC ET 200SP CPU | `6ES7 51x-…` | `SIMATIC S7 1500` |
| SoftPLC / S7-PLCSIM Adv. | （仿真目标） | `SIMATIC S7 1500` 或 `SIMATIC S7 1200`（取决于仿真 CPU 类型） |

匹配方式：MCP 实现按 PLC 设备 `TypeIdentifier` 中的「订货号」推断系列。**注意**：TIA 目录里订货号常带空格（如 `6ES7 211-1BE40-0XB0`）。旧版实现若只匹配无空格的 `6ES721…`，会误判为 UNKNOWN，连接仍显示默认的 **S7-300/400** 驱动。已在源码 `Portal.cs` → `InferUnifiedPlcFamilyFromSoftwarePath` 中去除空格后再匹配；**请重新编译 `TiaMcpServer.exe` 并替换交付包内同名可执行文件**（或直接用仓库 `tools/tiaportal-mcp` 下新生成的 Release 输出）。

若仍命中默认的 `SIMATIC S7 300/400`，应 **手工在 TIA 中切换** 为对应驱动后，再用 `DescribeObject` 读回校验。

## 根因速查（S7-1200 却显示 300/400）

| 根因 | 说明 |
|------|------|
| 订货号带空格 | `TypeIdentifier` 含 `6ES7 211…` 而非 `6ES7211…`，旧逻辑未识别 → 升级 MCP 可执行文件（见上） |
| 未连 PN 子网 | 仅影响在线，不总改驱动显示，但变量会红 | `ConnectDeviceNodesToProfinetSubnet` 或手工拖子网 |
| 枚举名地区差异 | 极少数安装语言下字符串写入失败 | TIA 内手动选驱动一次 |

## 关键参数

| 字段 | 取值规则 |
|------|----------|
| `Partner` | PLC 设备名（与 `GetProjectTree` 中 `Devices` 节点名一致，例如 `PLC_Main`） |
| `Station` | 多 CPU 项目下选 CPU 所在 Station |
| `Node` | PROFINET 接口名，例如 `PROFINET 接口_1` |
| `InitialAddress` | PLC PN 口 IP，例如 `192.168.0.1`（在线读取） |
| `CommunicationDriver` | 见上表 |

## 自检清单

1. `DescribeObject(HmiConnection)` 读回 `CommunicationDriver` 名称包含正确子串。  
2. HMI 与 PLC PN 口在 **同一子网**（`ConnectDeviceNodesToProfinetSubnet`）。  
3. PLC 已编译通过；HMI 标签所引用的 DB 已存在并编译。  
4. HMI 接口 DB 采用 **非优化（Standard）** 访问，便于以绝对地址寻址。

## 常见错配

| 现象 | 原因 | 处理 |
|------|------|------|
| 全列红 | 驱动落到 `SIMATIC S7 300/400`、PLC 实际是 1200/1500 | 在 TIA 中把 CommunicationDriver 切换到匹配项，保存后重读 |
| 仅部分 Tag 红 | 地址越界 / DB 编号或字节偏移错 | 用 `DBn.DBW/DBD` 字宽与对齐重核对 |
| 连接灰色 | Partner/Station/Node 三个字段未配齐 | 重建 `EnsureUnifiedHmiConnection` 或在 TIA 内补齐 |
