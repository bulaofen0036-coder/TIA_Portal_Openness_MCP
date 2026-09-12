# TIA Portal MCP — Natural-Language Intent Recipes

This document maps natural-language user intents to deterministic MCP tool sequences.
Every recipe ends with a readback step and a clear success criterion.

> **Status convention**: `verified` = tested on real hardware, `manual-derived` = derived from API knowledge, `probe-required` = needs live TIA test to confirm.

---

## Table of Contents

1. [新建项目并添加 PLC + HMI](#1-新建项目并添加-plc--hmi)
2. [打开现有项目并浏览结构](#2-打开现有项目并浏览结构)
3. [添加 PROFINET 硬件设备并组态网络](#3-添加-profinet-硬件设备并组态网络)
4. [创建 PLC 功能块 (FB) 并写入 SCL 逻辑](#4-创建-plc-功能块-fb-并写入-scl-逻辑)
5. [批量导出 PLC 程序并修改后重新导入](#5-批量导出-plc-程序并修改后重新导入)
6. [创建 PLC 全局数据块 (GlobalDB) 和变量表](#6-创建-plc-全局数据块-globaldb-和变量表)
7. [创建 Unified HMI 页面（按钮+指示灯+IO域）](#7-创建-unified-hmi-页面按钮指示灯io域)
8. [给 HMI 控件绑定 PLC 变量（动态化）](#8-给-hmi-控件绑定-plc-变量动态化)
9. [在线只读监控 PLC 变量值](#9-在线只读监控-plc-变量值)
10. [编译 PLC 并诊断报错](#10-编译-plc-并诊断报错)
11. [发布前验证（离线套件）](#11-发布前验证离线套件)
12. [从参考目录 Seed PLC 程序到新项目](#12-从参考目录-seed-plc-程序到新项目)
13. [编译并下载程序到 CPU（完整部署流）](#13-编译并下载程序到-cpu完整部署流)
14. [在线调试——强制/写入变量值](#14-在线调试强制写入变量值)
15. [报警文本批量更新（多语言 Excel 导入）](#15-报警文本批量更新多语言-excel-导入)
16. [配置 OPC UA 服务器接口](#16-配置-opc-ua-服务器接口)

---

## 1. 新建项目并添加 PLC + HMI

**用户说**: "帮我新建一个博途项目，加一个 S7-1200 CPU 和 KTP700 HMI 面板"

**工具序列**:
```
1. Connect
2. CreateProject(directoryPath, projectName)
3. AddDeviceWithFallback(preferredMlfb="", family="S7-1200", deviceName="PLC_1")
4. AddHardwareCatalogDeviceWithProbe(keyword="KTP700 Basic PN", deviceName="HMI_KTP700_1")
5. GetProjectTree                          ← 记录 PLC/HMI 路径
6. ConnectDeviceNodesToProfinetSubnet(firstRootPath="PLC_1", secondRootPath=<tree中HMI节点>, subnetName="PN_IE_1")
7. SaveProject
```

**成功判断**: GetProjectTree 显示 PLC_1 和 HMI_KTP700_1 均在同一子网下

**失败处理**:
- AddDeviceWithFallback 失败 → SearchHardwareCatalog 找确切 MLFB 后用 AddDevice
- ConnectDeviceNodesToProfinetSubnet 失败 → GetDeviceItemTree 检查端口路径，再试 EnsureSubnet + AttachDeviceNodeToSubnet

**Status**: `manual-derived`

---

## 2. 打开现有项目并浏览结构

**用户说**: "打开 D:\Projects\MyPlant.ap21 这个项目，看看里面有什么"

**工具序列**:
```
1. Connect
2. OpenProject(path="D:\Projects\MyPlant.ap21")
3. GetProjectTree                          ← 全貌
4. GetSoftwareTree(softwarePath="PLC_1")   ← PLC 块列表
5. GetHmiProgramInfo(softwarePath="HMI_RT_1")  ← HMI 画面列表
```

**成功判断**: GetProjectTree 返回非空树，GetSoftwareTree 列出块

**注意**: softwarePath 必须从 GetProjectTree 返回值中读取，不能猜测

**Status**: `verified`

---

## 3. 添加 PROFINET 硬件设备并组态网络

**用户说**: "项目里加一个 AFM60A 编码器，连到 PLC_1 的 PROFINET 网络"

**工具序列**:
```
1. SearchInstalledGsdDevices(keyword="AFM60A")   ← 确认已安装 GSD
2. AddGsdDeviceWithProbe(keyword="AFM60A", deviceName="ENC_AFM60A_1")
3. GetProjectTree                                 ← 取设备路径
4. ConnectDeviceNodesToProfinetSubnet(
     firstRootPath="PLC_1",
     secondRootPath=<tree中ENC_AFM60A_1的IE端口路径>
   )
5. SaveProject
```

**成功判断**: ConnectDeviceNodesToProfinetSubnet 返回 subnetName 和 nodePath 均非空

**失败处理**: GSD 未安装 → 需要在博途中手动导入 GSDML 后重试

**Status**: `manual-derived`

---

## 4. 创建 PLC 功能块 (FB) 并写入 SCL 逻辑

**用户说**: "帮我创建一个叫 FB_MotorControl 的功能块，有 Start/Stop 输入，Motor_On 输出"

**工具序列**:
```
1. GetSoftwareTree(softwarePath="PLC_1")   ← 确认目标路径
2. PlcBuildAndImport(
     kind="fb",
     softwarePath="PLC_1",
     json={
       "blockName": "FB_MotorControl",
       "blockNumber": 10,
       "inputs": [
         {"name": "Start", "datatype": "Bool"},
         {"name": "Stop",  "datatype": "Bool"}
       ],
       "outputs": [
         {"name": "Motor_On", "datatype": "Bool"}
       ],
       "statics": [
         {"name": "State", "datatype": "Bool"}
       ],
       "structuredText": {
         "operations": [
           {"op": "if",        "condition": "Start"},
           {"op": "assignment","target": "State", "literalValue": "TRUE"},
           {"op": "endif"},
           {"op": "if",        "condition": "Stop"},
           {"op": "assignment","target": "State", "literalValue": "FALSE"},
           {"op": "endif"},
           {"op": "assignment","target": "Motor_On", "literalValue": "#State"}
         ]
       }
     },
     dryRun=true                           ← 先验证
   )
3. PlcBuildAndImport(..., dryRun=false, compileAfter=true)  ← 正式导入
4. CompileAndDiagnosePlc(softwarePath="PLC_1")
5. GetBlockInfo(softwarePath="PLC_1", blockPath="Program blocks/FB_MotorControl")
```

**成功判断**: CompileAndDiagnosePlc errors=0，GetBlockInfo 返回 IsConsistent=true

**失败处理**: 编译报错 → ExportBlock 导出到工作目录 → 检查 XML 接口定义 → 修正后重新 PlcBuildAndImport

**Status**: `manual-derived`

---

## 5. 批量导出 PLC 程序并修改后重新导入

**用户说**: "把 PLC_1 的所有块导出到 D:\Export，修改后再导回来"

**工具序列（导出）**:
```
1. CompileAndDiagnosePlc(softwarePath="PLC_1")    ← 必须先编译才能导出
2. ExportBlocks(softwarePath="PLC_1",
                exportPath="D:\Export",
                regexName="",
                preservePath=true)
```

**工具序列（修改后导回）**:
```
3. ImportPlcProgramFromDirectory(
     softwarePath="PLC_1",
     importDirectory="D:\Export",
     compile=true
   )
4. CompileAndDiagnosePlc(softwarePath="PLC_1")
5. SaveProject
```

**成功判断**: ImportPlcProgramFromDirectory 报告 importedBlocks > 0，编译 errors=0

**注意**: 修改 XML 时不要改变块编号（BlockNumber），否则会创建重复块

**Status**: `manual-derived`

---

## 6. 创建 PLC 全局数据块 (GlobalDB) 和变量表

**用户说**: "创建一个叫 DB_MotorData 的 DB，有 Motor.Start、Motor.Stop、Motor.Speed 三个变量，再创建对应的 I/O 变量表"

**工具序列**:
```
1. PlcBuildAndImport(kind="globaldb", softwarePath="PLC_1", json={
     "dbName": "DB_MotorData",
     "dbNumber": 1,
     "staticMembers": [
       {"name": "Start", "datatype": "Bool", "startValue": "false"},
       {"name": "Stop",  "datatype": "Bool", "startValue": "false"},
       {"name": "Speed", "datatype": "Real", "startValue": "0.0"}
     ]
   }, dryRun=false, compileAfter=false)

2. PlcBuildAndImport(kind="tagtable", softwarePath="PLC_1", json={
     "tableName": "IO_Tags",
     "tags": [
       {"name": "StartBtn",  "dataTypeName": "Bool", "logicalAddress": "%I0.0"},
       {"name": "StopBtn",   "dataTypeName": "Bool", "logicalAddress": "%I0.1"},
       {"name": "MotorOut",  "dataTypeName": "Bool", "logicalAddress": "%Q0.0"}
     ]
   }, dryRun=false, compileAfter=false)

3. CompileAndDiagnosePlc(softwarePath="PLC_1")
4. SaveProject
```

**成功判断**: 两个 PlcBuildAndImport 均成功，编译 errors=0

**Status**: `manual-derived`

---

## 7. 创建 Unified HMI 页面（按钮+指示灯+IO域）

**用户说**: "在 HMI_RT_1 上创建一个叫 MotorCtrl 的页面，有启动/停止按钮和运行指示灯"

**工具序列**:
```
1. GetHmiProgramInfo(softwarePath="HMI_RT_1")     ← 确认是 Unified HMI
2. EnsureUnifiedHmiConnection(
     hmiSoftwarePath="HMI_RT_1",
     connectionName="HMI_Connection_1",
     plcName="PLC_1"
   )
3. EnsureUnifiedHmiScreen(hmiSoftwarePath="HMI_RT_1", screenName="MotorCtrl")
4. EnsureUnifiedHmiTagTable(hmiSoftwarePath="HMI_RT_1", tagTableName="默认变量表")
5. EnsureUnifiedHmiTag(hmiSoftwarePath="HMI_RT_1", tagTableName="默认变量表",
     tagName="StartPB", hmiDataType="Bool", plcName="PLC_1", plcTag="DB_MotorData.Start")
6. EnsureUnifiedHmiTag(... tagName="StopPB",  plcTag="DB_MotorData.Stop")
7. EnsureUnifiedHmiTag(... tagName="RunOut",  plcTag="DB_MotorData.Speed")
8. BuildUnifiedHmiLayoutDesignJson(layoutJson={
     "grid": true, "columns": 3, "cellWidth": 160, "cellHeight": 60, "gap": 12,
     "items": [
       {"name": "BTN_Start", "type": "Button",    "row": 0, "col": 0, "text": "启动"},
       {"name": "BTN_Stop",  "type": "Button",    "row": 0, "col": 1, "text": "停止"},
       {"name": "LMP_Run",   "type": "Rectangle", "row": 0, "col": 2, "text": "运行"}
     ]
   })
9. ApplyUnifiedHmiLayout(hmiSoftwarePath="HMI_RT_1", screenName="MotorCtrl")
10. SaveProject
```

**成功判断**: GetHmiScreens 包含 "MotorCtrl"，ApplyUnifiedHmiLayout success=true

`GetHmiScreens` 会递归列出根目录及所有子文件夹/子组中的画面（Classic、Unified 均支持）。
返回值仍为画面名称；描述、导出、编辑时直接传入该名称即可。
`EnsureUnifiedHmiScreen` 会先查找子组中的现有画面，找不到才在根目录创建。
名称匹配不区分大小写；如有同名画面，根目录匹配优先。

**Status**: `manual-derived`

---

## 8. 给 HMI 控件绑定 PLC 变量（动态化）

**用户说**: "把 MotorCtrl 页面上的 LMP_Run 指示灯绑定到 PLC 的 DB_MotorData.Start 变量"

**工具序列**:
```
1. EnsureUnifiedHmiTag(hmiSoftwarePath="HMI_RT_1", tagTableName="默认变量表",
     tagName="StartPB", hmiDataType="Bool",
     plcName="PLC_1", plcTag="DB_MotorData.Start")
2. EnsureUnifiedHmiDynamization(
     hmiSoftwarePath="HMI_RT_1",
     screenName="MotorCtrl",
     itemName="LMP_Run",
     propertyName="BackgroundColor"     ← 或 Visible/Text 等属性
   )
3. BindUnifiedHmiTagDynamization(
     hmiSoftwarePath="HMI_RT_1",
     screenName="MotorCtrl",
     itemName="LMP_Run",
     propertyName="BackgroundColor",
     tagName="StartPB"
   )
4. SaveProject
```

**成功判断**: DescribeHmiScreenItem 的 LMP_Run 属性中显示 dynamization 已绑定

**Status**: `probe-required`

---

## 9. 在线只读监控 PLC 变量值

**用户说**: "读一下 PLC_1 的 DB_MotorData.Speed 当前值"

**工具序列**:
```
1. PlanOnlineReadOnlyMonitoring(softwarePath="PLC_1", tags=["DB_MotorData.Speed"])
   ← 离线预检，确认路径合法
2. ProbePlcMonitorOnlineCapabilities(softwarePath="PLC_1")
   ← 探测在线 API 形状
3. GetPlcWatchTables(softwarePath="PLC_1")
   ← 找到包含目标变量的监控表
4. ReadPlcWatchTableCurrentValuesReadOnly(softwarePath="PLC_1", watchTableName=<表名>)
```

**成功判断**: ReadPlcWatchTableCurrentValuesReadOnly 返回 CurrentValue 非空

**重要限制**: 此配方为只读监控，不支持写入 PLC 变量值

**Status**: `probe-required`

---

## 10. 编译 PLC 并诊断报错

**用户说**: "编译一下 PLC_1，如果有错帮我看看"

**工具序列**:
```
1. CompileAndDiagnosePlc(softwarePath="PLC_1")
2. 判断结果:
   - errors=0, warnings=0 → 完成，SaveProject
   - errors=0, warnings>0 → 汇报警告内容，询问是否需要修复
   - errors>0 →
     a. 读取每条错误的 blockName 和 message
     b. ExportBlock(softwarePath="PLC_1", blockPath=<报错块路径>, exportPath=<工作目录>)
     c. 检查 XML 内容，定位接口/逻辑问题
     d. ComposePlcFbBlockXml / BuildPlcGlobalDbXml 修正
     e. ImportBlock(softwarePath="PLC_1", importPath=<修正后文件>)
     f. 重复 CompileAndDiagnosePlc 直到 errors=0
3. SaveProject
```

**成功判断**: CompileAndDiagnosePlc errors=0

**Status**: `verified`

---

## 11. 发布前验证（离线套件）

**用户说**: "项目要发布了，帮我跑一下验证"

**工具序列**:
```
1. RunOfflineReleaseValidationSuite(
     workspaceRoot="<repo根目录>",
     reportDirectory="<报告输出目录>"
   )
2. BuildReleaseDiagnosticReport(offlineReleaseSuiteJsonPath=<上一步生成的JSON>)
3. BuildReleaseRunbook(offlineReleaseSuiteJsonPath=<JSON>)
4. BuildReleaseManifest(offlineReleaseSuiteJsonPath=<JSON>)
5. 检查报告中的 pass/fail 条目，修复所有 fail 后重新运行
```

**成功判断**: 所有检查项 status=pass

**注意**: 离线套件不连接 TIA Portal，可在无博途环境下运行

**Status**: `manual-derived`

---

## 12. 从交付包模板 Seed PLC 程序到新项目

**用户说**: "把交付包里的通用 PLC 模板导入到新建项目"

**工具序列**:
```
1. GetSoftwareTree(softwarePath="PLC_1")   ← 确认目标 PLC 结构
2. 读取 templates/plc/plcbuild-json/*.json
3. 对每个模板执行 PlcBuildAndImport(
     softwarePath="PLC_1",
     kind="<模板 kind>",
     json="<模板 json 字段序列化后的字符串>",
     dryRun=true
   )
4. dryRun 全部通过后，按 tagtable → udt → globaldb → fc → fb 顺序执行 dryRun=false
5. CompileAndDiagnosePlc(softwarePath="PLC_1")
6. SaveProject
```

**LAD 调用网络**:
```
读取 templates/plc/lad-recipes/lad_call_recipes.json
使用 BuildFlgNetCallXml 生成调用网络 XML
检查 XML 后再选择合适的 ImportBlock / 组合块导入路径
```

**成功判断**: 编译 errors=0，GetSoftwareTree 中出现模板块名

**Status**: `manual-derived`

---

## 13. 编译并下载程序到 CPU（完整部署流）

**用户说**: "程序改好了，帮我下载到 PLC_1"

**工具序列**:
```
1. Connect
2. OpenProject(projectPath)
3. GetOnlineState(softwarePath="PLC_1")        ← 检查连接态（可选）
4. CompileSoftware(softwarePath="PLC_1")       ← 编译，确保无错误
5. CheckDownloadReadiness(softwarePath="PLC_1") ← 预检网络/配置
   └─ 如果 Ready=false，先查 Issues 解决网络问题
6. DownloadToPlc(
     softwarePath="PLC_1",
     keepActualValues=true,          ← 保留 DB 当前值（安全默认）
     consistentBlocksOnly=true,
     startAfterDownload=true
   )
7. GetOnlineState(softwarePath="PLC_1")        ← 验证下载后在线状态
8. SaveProject
```

**成功判断**: `DownloadToPlc` 返回 `State=Success` 或 `State=Warning`（无 Error），`GetOnlineState` 返回 `State=Online`

**安全提醒**:
- CPU 下载时会短暂停机，确认现场安全后再执行
- `keepActualValues=false` 会重置所有 DB 实际值，不可逆，慎用
- 下载前必须 `CompileSoftware` 成功

**Status**: `manual-derived`

---

## 14. 在线调试——强制/写入变量值

**用户说**: "把 DB1.DBX0.0 强制为 TRUE 测试一下"

**工具序列（Force，持续强制）**:
```
1. Connect
2. OpenProject(projectPath)
3. GetOnlineState(softwarePath="PLC_1")      ← 确认已联机
4. SetForceTableEntry(
     softwarePath="PLC_1",
     tableName="Debug_FT",
     address="DB1.DBX0.0",
     forceValue="TRUE"
   )
5. GetPlcForceTables(softwarePath="PLC_1")   ← 确认表已更新
   ← Force 在 TIA Portal 联机时自动生效
```

**工具序列（Modify，单次写值）**:
```
4. SetWatchTableModifyValue(
     softwarePath="PLC_1",
     tableName="Debug_WT",
     address="DB1.DBX0.0",
     modifyValue="TRUE",
     trigger="OnceOnlyAtStart"          ← 仅在扫描开始时写一次
   )
```

**清除强制**:
```
─ 在 TIA Portal UI 中手动清除强制表条目（公开 API 不支持编程清除）
─ 或：执行 DownloadToPlc 会自动清除所有强制
```

**成功判断**: `SetForceTableEntry` 返回 Message 中含 "ForceValue set"，TIA Portal 联机后变量显示强制值

**安全提醒**:
- Force 覆盖 PLC 逻辑，机器可能异常动作
- 仅在安全环境（模拟或隔离设备）下强制安全相关变量
- 调试完毕后断开在线连接或重新下载以清除强制

**Status**: `manual-derived`

---

## 15. 报警文本批量更新（多语言 Excel 导入）

**用户说**: "把翻译好的中英双语报警文本导入博途"

**工具序列**:
```
1. Connect
2. OpenProject(projectPath)
3. ExportAlarmTextLists(
     softwarePath="PLC_1",
     exportPath="C:\\Temp\\AlarmTexts_backup.xlsx"
   )                                          ← 先备份
4. ImportAlarmTextLists(
     softwarePath="PLC_1",
     importPath="C:\\Work\\AlarmTexts_CN_EN.xlsx"
   )                                          ← 导入翻译好的 Excel
5. CompileSoftware(softwarePath="PLC_1")      ← 验证报警配置
6. SaveProject
```

**配套工具**:
- `ExportAlarmClasses` / `ImportAlarmClasses` — 报警级别定义（严重度/颜色）
- `ExportAlarmInstanceTexts` — 导出实例级报警文本（含 InfoText、AlarmClass 列）

**Excel 格式说明**: 导出的 XLSX 包含每种语言一列，第一列为 ID/地址，直接编辑文本列后导入

**成功判断**: `ImportAlarmTextLists` 返回 `State=OK`，编译无报警相关错误

**Status**: `manual-derived`

---

## 16. 配置 OPC UA 服务器接口

**用户说**: "给 PLC_1 启用 OPC UA 服务器，然后导出接口定义"

**工具序列**:
```
1. Connect
2. OpenProject(projectPath)
3. GetOpcUaConfig(softwarePath="PLC_1")       ← 查看现有接口列表
4. SetOpcUaInterfaceEnabled(
     softwarePath="PLC_1",
     interfaceName="<ServerInterface 名称>",   ← 从步骤3结果中取名
     enabled=true,
     interfaceType="ServerInterface"
   )
5. DownloadToPlc(softwarePath="PLC_1")         ← 下载使能配置生效
6. ExportOpcUaInterface(
     softwarePath="PLC_1",
     interfaceName="<名称>",
     exportPath="C:\\Temp\\OpcUa_PLC1.xml"
   )                                           ← 导出接口定义供客户端使用
7. SaveProject
```

**导入自定义接口**:
```
4. ImportOpcUaInterface(
     softwarePath="PLC_1",
     importPath="C:\\Work\\MyOpcUaInterface.xml",
     interfaceType="ServerInterface"
   )
5. SetOpcUaInterfaceEnabled(softwarePath="PLC_1", interfaceName="MyOpcUaInterface", enabled=true)
6. DownloadToPlc(softwarePath="PLC_1")
```

**成功判断**: `GetOpcUaConfig` 中目标接口 `Enabled=true`，下载成功后 CPU 的 OPC UA 端口（默认 4840）可连接

**Status**: `manual-derived`

---

## 附录：常见错误码与处理

| 错误码 | 含义 | 处理方式 |
|--------|------|---------|
| `NotConnected` | 未连接博途 | 先调用 Connect |
| `ProjectNotOpen` | 无打开项目 | 调用 OpenProject 或 CreateProject |
| `InvalidParams` | 路径不合法或参数错误 | 用 GetSoftwareTree/GetProjectTree 取正确路径 |
| `NotFound` | 块/设备/标签不存在 | 用 GetBlocks/GetDevices 确认名称 |
| `CompileError` | 编译报错 | 按 Recipe 10 诊断流程处理 |
| `ExportFailed` | 块不一致无法导出 | 先 CompileAndDiagnosePlc |
| `HardwareCatalogNotFound` | 硬件型号未找到 | 先 SearchHardwareCatalog 确认 MLFB |
| `TiaSessionContention` | 会话冲突 | 不要同时运行 MCP 服务和 CLI 探针 |
