# Agent 编程与工具使用手册（TIA Portal V19）

> 给 AI agent 看的操作手册。目标：**用最少的 token 把事做对**。
> 只读这一份 + 需要时查 `docs/tools-index.csv`（233 个工具的一行索引），**不要**一次拉全部 233 个工具的描述。
> 默认 **lite** 档；要看/调用全部 233 个工具，用 `--profile full` 或 `TIA_MCP_PROFILE=full`。

---

## 0. 六条铁律

1. **每会话第一枪 `Bootstrap`**（只读，返回版本/连接状态/推荐下一步）。不要猜环境。
2. **写操作前先 dryRun**，导入后**立刻** `CompileAndDiagnosePlc`。
3. **路径和名称必须来自 TIA 读回**（`GetProjectTree` / `GetBlocksWithHierarchy`），不要自己拼。
4. **能从零建就用 CLI**：`tia-v19 gen spec.json` 一条命令 ≈ 20 次 MCP 调用，token 成本差一个量级。
5. **失败一次就停**：读错误码 → 查第 5 节 → 换参数再试。不要用同样的参数重试。
6. **大输出不进上下文**：不要 `GetSoftwareTree` 全量打印；先用 `GetBlocks`（扁平、短）。

---

## 1. 30 秒决策表

| 我要… | 用 |
|---|---|
| 看环境/版本/该干什么 | `Bootstrap` |
| 连 TIA | `Connect` |
| 新建空工程 | `CreateProject` |
| 打开已有工程 | `OpenProject`（或 `AttachToOpenProject` 附着已打开的） |
| 从零建整套工程 | **CLI `tia-v19 gen spec.json`**（首选） |
| 加 PLC/HMI | `AddDeviceWithFallback`（家族 + 首选 MLFB） |
| 查硬件型号 | `SearchHardwareCatalog` |
| 建变量表 / UDT / GlobalDB | `PlcBuildAndImport(kind=tagtable\|udt\|globaldb)` |
| 写 FC/FB 逻辑 | `.scl` 文件 → `ImportPlcExternalSource` → `GenerateBlocksFromExternalSource` |
| 写 LAD 调用网络 | `BuildFlgNetCallXml` → `ImportBlock` |
| 编译 + 看错误 | `CompileAndDiagnosePlc`（首选） |
| 读工程结构 | `GetProjectTree`（设备级） |
| 读块列表 | `GetBlocks`（扁平） / `GetBlocksWithHierarchy`（要分组路径时） |
| 读块逻辑（LAD/SCL） | `DescribeBlockLogic`（大响应用 `GetExport` 分页或 `SaveExport` 落盘） |
| 看大响应 / 分页 | `GetExport`（`exportId` + `nextOffset`） / `SaveExport` |
| 找不到工具 | `FindTools` → `CallTool` |
| 下载到 PLC | `CheckDownloadReadiness` → `DownloadToPlc`（**在线写，先确认安全**） |
| 读运行值 | `ReadPlcWatchTableCurrentValuesReadOnly` |
| 保存 | `SaveProject` |
| 收工 | `CloseProject` → `Disconnect` |

---

## 2. 三条黄金路径

### A. 从零建工程（推荐：CLI）

```
1. 写 spec.json（projectName / plcName / plcFamily / plcMlfb / tagTable / globalDb / compile）
2. tia-v19 gen spec.json --dry-run     # 离线校验，不碰 TIA
3. tia-v19 gen spec.json               # 建工程 + 加硬件 + 建 DB/变量表 + 编译 + 保存
4. tia-v19 describe <project.ap19> --plc PLC_1   # 读回验证（需要路径参数）
```

MCP 逐工具版本（需要交互式决策时才用）：

```
Bootstrap → Connect → CreateProject → AddDeviceWithFallback → GetProjectTree
→ PlcBuildAndImport(tagtable, dryRun=true→false) → PlcBuildAndImport(udt) → PlcBuildAndImport(globaldb)
→ [FC/FB: WritePlcSclSourceFile → ImportPlcExternalSource → GenerateBlocksFromExternalSource]
→ CompileAndDiagnosePlc → SaveProject
```

### B. 改已有工程

```
Bootstrap → Connect → GetProject → OpenProject(path) → GetBlocks（先看清单）
→ 改（PlcBuildAndImport / ImportBlock / ImportPlcExternalSource）
→ CompileAndDiagnosePlc → SaveProject
```

### C. 只读巡检（不改任何东西）

```
Bootstrap → Connect → GetState → GetDevices → GetSoftwareInfo → Disconnect
```

---

## 3. 工具地图（按域，不按字母）

**L0 只读（10 个）**：`Bootstrap` `Doctor` `GetState` `RunCapabilitySelfTest` `RunOnlineMonitoringSafetySelfTest` `GetAuthoringGuide` `CallTool` `FindTools` `GenerateAcceptanceReport` `GenerateErrorReport`

**L1 主力（45 个，按域）**

| 域 | 工具 |
|---|---|
| Portal | `Connect` `Disconnect` `EnsureOpennessUserGroup` `ListPortalProcessProjects` |
| 工程 | `CreateProject` `OpenProject` `AttachToOpenProject` `CloseProject` `SaveProject` `GetProject` `GetProjectTree` `GetState` |
| 硬件 | `AddDeviceWithFallback` `GetDevices` `SearchHardwareCatalog` `ConnectDeviceNodesToProfinetSubnet` |
| PLC 软件 | `PlcBuildAndImport` `ImportBlock` `ImportType` `ImportPlcTagTable` `CompileAndDiagnosePlc` `CompileSoftware` `GetSoftwareInfo` `GetSoftwareTree` `WritePlcSclSourceFile` `DescribeBlockLogic` `ValidateAutomationContext` |
| 在线 | `GoOnline` `GoOffline` `GetOnlineState` `CheckDownloadReadiness` `DownloadToPlc` |
| 大响应 | `GetExport` `ListExports` `SaveExport` `DeleteExport` `ClearExports` |

**L2（178 个）**：构建器、HMI、导出、报表、诊断、日常编辑、VCI（V19 不编译）。用之前先在 `docs/tools-index.csv` 里搜名字（或查 `docs/TOOLS_REFERENCE.md` 的完整说明），**不要凭记忆猜**。
高频 L2：`BuildPlcUdtXml` `BuildPlcGlobalDbXml` `BuildPlcTagTableXml` `BuildFlgNetCallXml` `ImportPlcExternalSource` `GenerateBlocksFromExternalSource` `GetBlocks` `GetBlocksWithHierarchy` `GetBlockInfo` `ExportBlock` `GetPlcTagTables` `GetTypes` `GetHmiScreens` `ApplyUnifiedHmiScreenDesignJson`。

---

## 4. 参数铁律（最常错的地方）

| 参数 | 规则 | 例 |
|---|---|---|
| `softwarePath` | 设备名，不是路径 | `PLC_1` |
| `blockPath` | **全路径**，来自 `GetBlocksWithHierarchy` | `Group/Sub/FB_Start` |
| 绝对地址 | `%M0.0` `%MW10` `%MD20` `%DB100.DBX0.0` | `%DB100.DBW2` |
| `importPath` | 绝对路径，文件必须存在 | `C:\work\b.xml` |
| `json` | **是字符串**，里面再套 JSON（注意转义） | `"{\"dbName\":\"DB_1\"}"` |
| `.scl` 文件 | **UTF-8 with BOM**（否则中文注释乱码） | 用 `WritePlcSclSourceFile` 生成 |
| 块号 | 全局唯一；建议 100+ 段 | `FB 110` |

> 路径/名称错一次，后面全是连锁错误。**先 `GetProjectTree`，再动手。**

---

## 5. 报错 → 下一步

| 现象 | 原因 | 处理 |
|---|---|---|
| `NotSupportedOnVersion` | 该功能 V19 没有 | 换方案（见第 6 节），不要重试 |
| `InvalidParams` / 找不到对象 | 路径或名称不对 | `GetProjectTree` / `GetBlocksWithHierarchy` 读回真实值 |
| 编译 `errorCount > 0` | 代码问题 | 读 `Errors[]` 的块名+行号 → 改 → 重导 → 再编译 |
| `Connect` 失败 | 不在 Openness 组 / TIA 未装 | `EnsureOpennessUserGroup`，加组后**注销重登** |
| 导入成功但编译报“块不存在” | 导入到了别的组 | 用 `GetBlocks` 确认实际路径 |
| 返回空列表但不报错 | 路径为空或对象不存在 | 不要当成功；用 `GetProjectTree` 交叉验证 |
| HTTP 客户端连不上 | 绑了 127.0.0.1 / 防火墙 / URL ACL | 绑具体网卡 IP，见 README.V19 |

---

## 6. V19 能力边界（必须记住）

| 功能 | V19 | 替代 |
|---|---|---|
| SIMATIC SD / S7DCL 文档导入导出 | ❌ 无 API | 用 `.scl` 外部源导入 |
| `ExportAsDocuments` / `ImportBlocksFromDocuments` 等 4 个工具 | ❌ 返回 `NotSupportedOnVersion` | 同上 |
| VCI（版本控制接口，5 个工具） | ❌ 不编译 | 用 Git + 手工导出/导入 |
| 硬件级 HMI 连接创建（V21+） | ⚠️ 降级 | 走软件层；经典面板可能建不出 |
| HMI 读属性 / HMI 编译 / 离散报警 | ⬜（编译通过，未真机） | 先小规模试 |
| Cax/AML 设备导出、软件单元、日常编辑 | ⬜（编译通过，未真机） | 先小规模试 |
| SCL 外部源 FC/FB | ✅（2.2.1 未真机验证） | — |
| 经典 / Unified HMI | ⬜（未验证） | 先小规模试 |
| 编译 / DB / 变量表 / UDT | ✅（2.2.1 已实测；合并后待重跑） | — |
| 读块逻辑（LAD/SCL） | ✅（编译 + 离线测试） | `DescribeBlockLogic`；大响应 `GetExport`/`SaveExport` |

---

## 7. SCL 编程基础（够写出能编译的块）

**块类型**：`OB`（周期/中断）、`FC`（无记忆）、`FB`（有静态记忆，配背景 DB）、`DB`（数据）。

**数据类型**：`Bool` `Byte` `Word` `DWord` `Int` `DInt` `Real` `LReal` `Time` `Char` `String` `Array[1..10] of Int` `Struct`。

**最小骨架**

```scl
FUNCTION_BLOCK "FB_StartStop"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Start  : Bool;
      Stop   : Bool;
      Fault  : Bool;
      Reset  : Bool;
   END_VAR
   VAR_OUTPUT
      Run    : Bool;
   END_VAR
   VAR
      Latch  : Bool;
      T1     : TON;
      Edge   : R_TRIG;
   END_VAR

BEGIN
   // 故障优先，复位清故障
   IF #Fault THEN
      #Latch := FALSE;
   ELSIF #Start AND NOT #Stop THEN
      #Latch := TRUE;
   END_IF;

   // 定时器（IN 上升沿开始计时，Q 到点置位）
   #T1(IN := #Latch, PT := T#5S);
   #Edge(CLK := #T1.Q);

   #Run := #Latch;
END_FUNCTION_BLOCK
```

**语法要点**

- 每条语句 `;` 结尾；赋值 `:=`；比较 `=`；逻辑 `AND` `OR` `NOT`；取反 `<>`
- `IF … THEN … ELSIF … ELSE … END_IF;`
- `CASE #Step OF 0: …; 1: …; ELSE …; END_CASE;`
- `FOR #i := 1 TO 10 DO … END_FOR;`
- 定时器/边沿必须先声明实例（`TON` / `R_TRIG`），再当函数调用
- 类型不混用：`Real` 与 `Int` 运算要显式转换（`INT_TO_REAL`、`REAL_TO_INT`）
- FB 的局部变量加 `#`；全局 DB 用 `"DB_Name".Member`

**命名与编号**：`FB_` / `FC_` / `DB_` / `UDT_` 前缀；块号 100 起，避免和系统块冲突；一个块只做一件事。

**现成模板**（已验证可编译，优先抄结构，不要从零写）：

- `templates/plc/scl-examples/FB_BasicLatch.scl` — 启停/故障/复位自锁
- `templates/plc/scl-examples/FB_TimerCounterDemo.scl` — TON + 上升沿计数
- `templates/plc/scl-examples/FB_StepSequenceDemo.scl` — CASE 步序状态机
- `templates/plc/scl-examples/FC_BasicScaleLimit.scl` — 线性缩放 + LIMIT
- `templates/plc/plcbuild-json/*.json` — 变量表 / UDT / DB 的 DSL 样例

---

## 8. 自进化协议（每次任务都执行）

目标：让下一次会话比这一次更省 token。知识库是 `docs/AGENT_LEARNINGS.md`。

**会话开始时**：只读该文件**最后 40 行**（不要全文）。

**任务结束时**（成功或失败都算），如果产生了可复用结论，追加一行：

```
- [YYYY-MM-DD] 场景 → 结论（证据：错误码/输出/工具返回）
```

**硬规则**

1. 只写**验证过**的结论；猜测写在句尾标 `?`，下次验证后再去掉。
2. 一条一事，≤ 2 行；不写项目名、IP、客户名、密钥。
3. **同一结论出现 3 次** → 升级为规则：写进本文件对应章节，并在 LEARNINGS 里标 `[promoted]`。
4. 结论被推翻 → 不要删除，追加 `[corrected]` 并写新结论（保留演化痕迹）。
5. 每次只追加，不改历史（append-only）。

**什么时候值得写**

- 某个工具的参数格式踩过坑 ✅
- 某个版本不支持某功能 ✅
- 某条调用顺序能稳定跑通 ✅
- 一次性的项目数据 ❌
- 从文档里抄来的、没验证过的东西 ❌

---

## 9. Token 预算参考

| 动作 | 大约 token |
|---|---|
| `Bootstrap` 返回 | 1–2k |
| `GetProjectTree`（10 设备） | 1–3k |
| `GetSoftwareTree`（大工程） | 5–20k ⚠️ 尽量别用 |
| `GetBlocks`（扁平） | 0.5–2k |
| `CompileAndDiagnosePlc`（干净） | 0.3k |
| 一次 `PlcBuildAndImport` 的大 JSON | 1–5k ⚠️ 用文件 + CLI 更省 |

**省 token 的三个习惯**：先 `GetBlocks` 再决定要不要树；大 JSON 走 CLI 文件；失败只读错误行，不回读全量输出。
