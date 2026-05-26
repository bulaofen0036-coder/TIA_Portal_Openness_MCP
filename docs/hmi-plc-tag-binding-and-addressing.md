# HMI ↔ PLC 变量绑定（默认采用绝对地址）

本包统一以 **绝对地址** 方式建立 WinCC Unified 与 PLC 的变量互连。原因：
- 工程交付后，**绝对地址唯一、可读、可校核**；
- 在 MCP / Openness 自动化中，**符号互连** 受「优化访问 / 通讯驱动 / 名称解析」等多重影响，红字概率偏高；
- 绝对地址要求 PLC 侧 **非优化访问** 的全局 DB，与团队常规交付习惯一致。

## 1. 必备前提

| 项 | 取值 |
|----|------|
| HMI 接口 DB | 名称：`DB_HMI_Interface`，编号：`200`，**`MemoryLayout = Standard`（非优化）** |
| 字节排布 | 见 `templates/plc/plcbuild-json/db_hmi_interface.json` 中的 `absoluteLayout` 段 |
| 通讯驱动 | 见 `docs/hmi-connection-driver-matrix.md` |
| 子网 | PLC PN 口与 HMI PN 口在同一 PROFINET 子网 |
| 编译 | PLC 编译错误为 0；HMI 编译错误为 0 |

## 1.1 MCP `EnsureUnifiedHmiTag` 与符号 `DB_…`（已修实现缺陷）

若 `plcTag` 传入 **`DB_HMI_Interface.CmdEnable`** 这类**符号**（块名以字母开头），旧版 `TiaMcpServer` 曾错误地把「凡以 `DB` 开头的字符串」都当成绝对地址，从而把访问模式写成 **Absolute**、并把整串符号塞进 **Address** 字段，**与 PLC 符号表无法对应**，表现为红字或值不刷新。  

正确规则：**符号**形如 `DB_MyBlock.Member`；**绝对**形如 `%DB200.DBX0.0` 或 `DB200.DBX0.0`（`DB` 后紧跟数字）。已在 `Portal.cs` → `BindUnifiedHmiTagToPlcSymbol` 中修正判定；请使用**新编译的** `TiaMcpServer.exe`。

## 2. 绝对地址清单（与蓝图一致）

| HMI Tag | 数据类型 | 绝对地址 |
|---------|-----------|-----------|
| `HMI_CmdEnable`     | Bool | `%DB200.DBX0.0` |
| `HMI_CmdDisable`    | Bool | `%DB200.DBX0.1` |
| `HMI_CmdReset`      | Bool | `%DB200.DBX0.2` |
| `HMI_CmdApply`      | Bool | `%DB200.DBX0.3` |
| `HMI_StatusActive`  | Bool | `%DB200.DBX1.0` |
| `HMI_StatusError`   | Bool | `%DB200.DBX1.1` |
| `HMI_StatusWarning` | Bool | `%DB200.DBX1.2` |
| `HMI_StepNo`        | Int  | `%DB200.DBW2` |
| `HMI_ValueSetpoint` | Real | `%DB200.DBD4` |
| `HMI_ValueActual`   | Real | `%DB200.DBD8` |
| `HMI_ValueOutput`   | Real | `%DB200.DBD12` |
| `HMI_OutputMin`     | Real | `%DB200.DBD16` |
| `HMI_OutputMax`     | Real | `%DB200.DBD20` |
| `HMI_CounterPreset` | DInt | `%DB200.DBD24` |
| `HMI_CounterValue`  | DInt | `%DB200.DBD28` |

地址排布规则：

- Bool 在字节内位寻址（`.0`～`.7`）。
- Int 占 2 字节，按 **字** 对齐；Real / DInt 占 4 字节，按 **双字** 对齐。
- Bool 之间若跨段，需保留对齐填充位（蓝图模板已经预留 `_pad1_0_4` ～ `_pad1_0_7`）。

## 3. 在 MCP 中创建变量（绝对地址）

`EnsureUnifiedHmiTag` 当前实现接受 `plcTag`（符号）参数；为强制使用绝对地址，操作顺序：

1. 调用 `EnsureUnifiedHmiTag` 创建变量，先 **不带 PLC 绑定**：仅 `hmiSoftwarePath`、`tagTableName`、`tagName`、`hmiDataType`、`connectionName`。
2. 紧接着用 `InvokeObject` 或 `DescribeObject` 写入该 HMI Tag 的 `LogicalAddress` 属性，值为上表中的 `%DBn.DBxx.x` / `%DBn.DBWnn` / `%DBn.DBDnn`。
3. 用 `DescribeHmiTag` 读回 `LogicalAddress` 进行校验。

> 备注：不同 MCP 构建对 `LogicalAddress` 命名可能略有差异（如 `Address`、`PlcTagSymbolic`），通过 `DescribeObject(HmiTag)` 查询并选用 **可写** 的字符串属性。

## 4. 故障速查

| 现象 | 处理顺序 |
|------|----------|
| 单个 Tag 红 | 核对地址字宽与字节对齐；非优化 DB 是否被改成优化 |
| 全部 Tag 红 | 核对 CommunicationDriver、Partner、Node、子网；PLC 是否在线 |
| 在 TIA 中改为符号 | 取决于现场要求；若改回符号绑定，请确保 DB 名/成员名稳定 |

## 5. 与符号互连的关系

符号互连不是错，只是 **默认不用**。如果团队明确需要符号互连（例如优化访问 DB、跟随成员重命名），需要：

- 在 TIA 中确认 PLC 端符号编译通过；
- HMI 端 `plcName` 与 `GetProjectTree` 中 PLC 软件节点名一致；
- 测试 `DescribeHmiTag` 中的解析状态不再红字。

完成上述条件后，可同时保留绝对地址作为「灾备」字段，便于后续核对。
