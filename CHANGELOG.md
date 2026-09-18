# Change Log

## [Unreleased] - HMI tag and screen-item PROPERTIES are readable in one call

### 新增

- **`GetHmiTagDetails` / `GetHmiScreenItemDetails`: read the attribute VALUES of HMI tags and
  screen items, per tag table respectively per screen, in ONE call.** The server could already
  list their names (`GetHmiTags`, `ListObjectChildren`) and describe their API shape
  (`DescribeHmiTag`, `DescribeHmiScreenItem`), but a value — datatype, connection, address, PLC
  binding, position, size — could only be fetched one attribute per object per call through the
  `InvokeObject` reflection bridge. For a project with 223 tags and 26 screens that is thousands
  of round trips, over a tool that can also write and therefore cannot be handed to a read-only
  review agent. Both new tools are read-only by construction: no `allowWrite`, no write path.

  - `GetHmiTagDetails(softwarePath, tagTableName?, attributes?)` — one tag table, or every tag
    table of the HMI when `tagTableName` is empty, **including tables filed in a
    `TagTableGroup`** (the tag counterpart of the screen-group walk from PR #41). Default
    attributes: `Name, DataType, HmiDataType, Connection, Address, PlcTag, PlcName,
    AcquisitionCycle`.
  - `GetHmiScreenItemDetails(softwarePath, screenName, attributes?)` — every item on one screen
    with its CLR type (`HmiButton` / `HmiIOField` / `HmiText` / …) plus **the screen's own size**,
    without which an item's position cannot be judged. Default attributes:
    `Name, Left, Top, Width, Height, Visible, Enabled`.

  Three properties they are built to keep, each one a failure that would otherwise stay silent:

  - **An attribute this object type does not have blanks that one value, not the call.** Openness
    throws for an unsupported attribute name; swallowing that per attribute is what lets one
    exotic column coexist with seven good ones.
  - **Every value that is not a JSON primitive is flattened to a string.** A `MultilingualText`
    (`Text`, `ToolTipText`, `DisplayName`) carries `Items[].Language.Culture.Parent.Parent…`;
    serializing it raises "a possible object cycle was detected" at depth 64 and takes the whole
    response with it. One exotic attribute must not be able to sink a 200-item inventory.
  - **`success` follows the failure count, and nothing is dropped.** An object that cannot be read
    is listed in `Failed[]` with a reason; `Meta.success` is false as soon as anything failed. The
    pattern this avoids is already in this server twice (`ExportHmiProgram` reports
    "HMI program exported" with all 41 objects in `Failed[]` and `success: true`;
    `ExportAlarmClasses` reports "not available for this PLC" the same way) — a caller that
    trusts `success` files an empty inventory as a complete one.

### 修复

- **`InvokeObject` / `InvokeService` refused `GetAttributes`, the one call that reads several
  attributes at once.** It was answered with "Method not allowed (read-only mode)" although it
  provably only reads, which left one call per attribute as the only route. It is now on the
  read-only allowlist — and, because letting it through alone would not have made it work:

  - a nested JSON array is now ONE argument and becomes a string list (it used to arrive as the
    literal text `["Address","PlcTag"]`, which no `IEnumerable<string>` parameter accepts), while
    an array handed to a `string` parameter still arrives as that same JSON text as before;
  - overloads are selected by parameter TYPE, not by parameter count alone — `GetAttributes` has
    two overloads that each take exactly one parameter, so counting picked whichever the runtime
    happened to return first;
  - an `enum` parameter is parsed from its name instead of being passed as a string.

  `SetAttribute`/`SetAttributes` stay refused; the allowlist moved into the zero-dependency
  `ReflectionInvokeSafety.cs` so the offline suite can hold that line.

  This is a cheaper route for every caller, but it does **not** replace the two tools above:
  `InvokeObject` can still write, so a read-only agent must not be given it.

## [2.7.3] - 2026-09-16 - 写 Unified JS 脚本不再赌上整个博途进程；画面分组里的画面不再隐形

### 修复

- **写完 Unified HMI 的 JS 事件脚本就强制跑 `SyntaxCheck()`，在 TIA V21 上会偶发
  把博途进程整个带走**（issue #36，由 Moy-GH 报告）。抛的是 `NonRecoverableException`，
  不是「这一步失败了」而是「进程没了」：句柄全部作废，刚写进去、还没保存的脚本
  随之消失。而这个检查从来不是这次调用的目的 —— 目的是把脚本写进去。
  现在 `SetUnifiedHmiButtonEventScriptCode` 与 `EnsureUnifiedHmiButtonAction`
  都新增 `syntaxCheck` 参数，**默认 false**，不再跑这个检查。

  三处配套改动，都是为了不让「没做」看起来像「做过了」：

  - **不查的时候绝不发 `syntaxErrorCount`。** 发一个 `0` 出去等于把「没检查」
    谎报成「检查通过」。改为显式给出 `syntaxCheckStatus=skipped` 与跳过原因，
    键缺席只能读成「没查」。
  - **检查真把进程干掉时，不再返回成功。** 原来这个异常被 `catch` 吞进
    `meta["syntaxError"]`，然后照常返回「ScriptCode set」—— 而实际上那份脚本
    已经跟着进程一起没了。现在识别出「进程级致命错」就如实报不确定，
    并给出恢复步骤（重连、用 `syntaxCheck=false` 重写、保存）。
  - **ScriptCode 写失败的判断挪到检查之前。** 原来排在后面，等于先拿一个已知
    会弄崩 V21 的调用，去检查一份根本没写进去的脚本。

  需要 TIA 自己的语法结论时显式传 `syntaxCheck=true`（`--validate-unified-hmi-action-syntaxcheck`
  这条以取证为目的的 CLI 就是这么做的）。只想检查脚本写得对不对，用离线的
  `BuildUnifiedHmiButtonActionScript`，它不连博途，崩不了任何东西。

- **Screens inside a Unified HMI screen group (folder) were invisible to every
  screen tool.** (PR #41, contributed by @DenLucas) `GetHmiScreens`, `GetHmiProgramInfo`, `DescribeHmiScreen`,
  `ExportHmiScreen`, `ExportHmiProgram`, `EnsureUnifiedHmiScreen` and the
  button-action bind tool all resolved screens by reading only the HMI
  software's root `Screens` composition. A screen filed under a `ScreenGroups`
  folder (the normal way real WinCC Unified projects organize screens, e.g.
  `00_ScreenLayout/PopUps/LogOff`) was not found by name, not listed, and not
  exported — with no indication that a folder was the reason, only a plain
  "not found". Moving a screen to the HMI software's root made it visible
  again, which was the only symptom that pointed at the real cause.

  `TryListScreens` and every screen-by-name lookup now walk a shared
  recursive helper (`EnumerateHmiScreensRecursive` /
  `TryFindScreenByNameRecursive`, mirroring the existing
  `EnumeratePlcWatchTablesRecursive` pattern used for nested PLC watch/force
  table groups) that also descends into `ScreenGroups` — and, for Classic
  HMI, `ScreenFolder`/`Folders`/`Groups` — with cycle protection. Verified
  against a real TIA Portal V21 project with `LogOff`/`ModeRequests`/
  `PowerStatus` two levels deep in a screen group: all three now resolve by
  name and appear in `GetHmiScreens`. Not separately verified against a
  Classic (Comfort/Basic) HMI project with nested screen folders — that path
  follows the same existing `ScreenFolder` property names already used
  elsewhere in this file, but I only had a Unified project to test against.

  Maintainer follow-up on the same PR: two more lookups had the same shallow
  read — the reflection bridge's `objectKind=HmiScreen` / `HmiScreenItem`
  (`DescribeObject`, `GetObjectProperty`, `InvokeObject`, …) — and now go
  through the same walk. The walk moved into the zero-dependency
  `HmiScreenWalk.cs` so the offline suite can feed it fake object graphs
  (Unified two-level groups, Classic nested folders, a self-referencing group,
  a collection that throws mid-enumeration); a lookup that fails still reads as
  "not found" instead of throwing, as before. The property names were checked
  against the V21 Openness assemblies: `HmiSoftware.ScreenGroups` →
  `HmiScreenGroup.Groups`/`Screens`, and `HmiTarget.ScreenFolder` →
  `Folders`/`Screens`. Classic popup / slide-in / template folders are still
  not walked, same as before this change.

## [2.7.2] - 2026-09-05 - 寻不到址的模块、用不了的服务器、不说话的空结果

三条都来自 issue #33 的现场反馈，都是「工具在，但用不上」。

### 修复

- **名字里含 `/` 的设备项，从原理上寻不到址。** `deviceItemPath` 用 `/` 分段，
  而西门子的模块名本身常常自带 `/`：`DQ 16x24VDC/0.5A ST_1`、
  `AI 4xI 2-/4-wire ST_1`、`AQ 4xU/I ST_1`、`DI 6/DQ 4_1`。逐段比对的话，
  这些模块**不是找不到，是根本表达不出来** —— 所有按设备项路径取对象的硬件工具
  对它们一律失效。现在匹配子项时会把后续若干段拼回 `a/b/c` 再比，跨度从大到小试，
  先匹配更长（更具体）的那个。子项名是已知的有限集合，所以这不是猜；
  既不需要引入转义语法，也不改变原来能用的写法。

- **设备项本级没有 I/O 地址时，只回一句「没有任何 I/O 地址」。**
  地址在分布式 I/O（ET200 系列）和机架式站上常常挂在**子项**而不是模块对象本身，
  只说「没有」会让人以为工具读不到，转头去查一个没问题的组态。现在本级为空时
  会下钻一层，把**子项里有地址的**连同地址摘要一并列出来，可以直接换路径重试。

- **`GetPlcTagTables` 返回空清单时，答不上来是哪一种空。** 三种成因返回的东西
  一模一样：这个 PLC 确实没有表 / `TagTables` 属性在这个版本上叫别的名字 /
  读属性时抛了异常被吞掉。现在空清单会带上这次遍历的证据
  （`walkedGroupType` / `tagTablesPropertyFound` / `tagTablesPropertyError` /
  `groupsVisited` / `notes`），并在消息里明说「这不一定表示它没有表，
  读属性失败也长这样」。有表时行为一字不变，也不掺诊断字段。

### 新增

- **`ConnectIsolated`** —— 起一个全新的无头 TIA Portal 实例，不 attach、不修改、
  不关闭任何用户已经开着的窗口或工程。

  这条是修一个**把人挡在门外**的组合缺陷：`Connect` 会优先接管**已经开着工程**的
  那个实例，而 `OpenProject` 又（正确地）拒绝动别人的工程 —— 两条各自正确的行为
  叠在一起，结果是**只要用户在博途界面里开着任何工程，整台服务器就用不了**。
  有了这条路，用户可以一边在界面里干活，一边让 MCP 在自己的实例里跑自己的工程。

  必须是该 MCP 进程里的第一个连接动作：已经绑了别的连接再起隔离实例，只会留下
  一个没人管的 portal 进程，之后别的 attach 会把它抢走，表现为间歇性的
  「no open project」。已进 lite 默认档 —— 挡掉这条出口等于让「用户正在用博途」
  变成一个无解的死局。

### 验证

真机跑在真实工程副本上（S7-1200 + S7-1500，V21），**全程在隔离实例里，
没有碰当时正开着的用户工程**（事后核对 portal 进程为证），合并 22/22：

含 `/` 的模块全路径与简写都能读出地址；CPU 本级无地址时列出
`PROFINET 接口_1` / `HSC_1..3` 及各自地址；`ConnectIsolated` 起实例并回读句柄确认、
重复调用被拒；2.7.1 那批「假成功」修复无回归（7 个工具对错路径仍明确报错、
4 条正确输入照旧成功）。反向哨兵覆盖：**拼出来的错名字仍必须报错**
（防贪心匹配把错路径吃成对的）、普通模块照旧、`GetDeviceItemTree` 未被改坏、
有变量表时不掺诊断字段。

**V20 也验了**（V20 引擎 + `--tia-major-version 20` + 一个 V20 工程副本，
S7-1200 + S7-1500），8/8。这轮顺带更正了 issue #33 里的两个推测：

- **V20 读得到模块 I/O 地址**：同一台 S7-1200 上 `AI 2_1` 读到 1 条、
  `DI 6/DQ 4_1` 读到 2 条。所以「V20 的 Openness 不填充模块 I/O 地址」
  不是一条普遍限制 —— 那更可能是分布式 I/O（ET200SP）特有的形态，
  地址挂在子项上，本版新增的子项提示正好用来定位它。
- **V20 的 `GetPlcTagTables` 枚举正常**：两个 PLC 分别列出 1 张和 5 张表。
  所以枚举返回空也不是 V20 的普遍问题，是某个具体工程/软件对象的形态问题 ——
  本版新增的遍历证据字段就是为了把这种情况钉住。

**仍未验到的部分，如实说明**：手上的工程里没有 **ET200SP** 这种分布式 I/O 站，
子项下钻是在 S7-1200 CPU 上验的（同一段代码，但没覆盖 ET200SP 的实际形态）。
变量表枚举的**空清单分支**在这些工程上触发不到（每个 PLC 都有默认表），
改用临时插桩确认了证据字段确实被采集，插桩已撤除。

工具 221 → 222，lite 54 → 55（仍在 128 上限内）。离线套件 126/126，死引用闸门 PASS。

## [2.7.1] - 2026-09-05 - 「路径写错却报成功」的一轮扫荡

这一版没有新功能，全是修复，而且是同一族的：**把「没做成」说成一个确定的结论**。

这类缺陷比崩溃危险得多。崩溃会让调用方停下来；一句「找到 0 个」「已下线」
「0 处差异」会让它**继续往下走**，并把这个错误结论当作事实带进后面每一步。
对着 AI 客户端尤其如此——它没有第二个信息源来质疑你。

### 怎么找出来的

新增发版前闸门 `scripts/Sweep-WrongPathHonesty.py`：拿真项目给**每个只读工具**
喂一条不存在的路径，凡是「返回成功」的都列为嫌疑。第一轮跑出 10 个。

这条检查**离线跑不了**（要判断"路径不存在时的反应"就得有真项目），所以它不在
CI 里，是发版前的手工闸门。脚本自带反向哨兵：正确输入必须仍然成功——否则
「所有工具一律报错」的坏实现也能满分通过，那时它测的就不是诚实，是「有没有全坏」。

### 修复

- **`CompareSoftwareToOnline`** —— 这批里最贵的一条。这个工具的全部价值就是回答
  「要不要下载」。路径写错时它返回 `Entries=null` 的正常响应，调用方读到「0 处差异」，
  得出「在线离线一致，不用下载」。比对**失败**时同样如此，而且其中一条路径还硬写
  `IsOnline=true`——实测拿到的正是「The operation is not permitted in offline mode」，
  即根本没在线，字段和消息自相矛盾。现在三条路径都明确失败，并写明
  「NO comparison result — 不要当成 identical」。
- **以 `Identical` 结尾的比对状态被算成差异**，把「一致」报成
  「Compare complete: N differences」，可能诱发一次不必要的下载。
- **`GoOffline`** —— 路径写错、或 `OnlineProvider` 拿不到（那一路 `provider?.GoOffline()`
  **什么都没做**），都回一句「'X' is now offline」。调用方据此认为在线会话已断开，
  实际它还挂着：后续 `CompileSoftware` / `Export*` 会被「not permitted in online mode」
  挡住，现场 CPU 的编程连接也一直被占。
- **`GetSoftwareTree`** —— 路径不存在时把一句「No PLC software found」当作**树的内容**
  返回，工具层只判非空即算成功，于是 `message` 写着「Software tree retrieved from 'XXX'」
  而树体里写着找不到。同一个 `catch` 还把异常文本也当成树返回，**遍历炸在半路也报成功**。
  另外没连项目时返回空串，报出的是 `InternalError`「Failed retrieving」——最常见的一种错
  （忘了 Connect）拿到的是最没用的一句话。
- **`GetTechnologyObjects`** —— 「没连项目」「路径写错」「枚举炸了」三条全返回空列表，
  报成「在 'XXX' 里找到 0 个技术对象」。空列表只有一个合法含义：
  解析到了这个 PLC，它确实没有 TO。
- **`GetOnlineState` / `GoOnline`** —— 对不存在的 PLC 返回 `State="Offline"`。这是
  **给一个没测过的问题一个确定的答案**，调用方会当成「已确认不在线」。拿不到
  `OnlineProvider` 时改报 `State="Unknown"` 并说明「未测量 ≠ 离线」。
- **`GetPlcForceTables`** —— `Items=[]` + 一句 not found 的正常响应。
- **反射桥 7 处**（`DescribeObject` / `DescribeObjectProperty` / `GetObjectProperty` /
  `ListObjectChildren` / `InvokeObject` / `DescribeService` / `InvokeService`）和
  **HMI Describe 族 13 处** —— 返回 `Message="Object not found"` + 空成员表。
  反射桥恰恰是**用来猜路径**的工具，猜错必须响，否则每次猜错都被记成一条
  「已确认为空」的事实，越猜越偏。这些路径现在报的是参数错误（`InvalidParams`），
  不再是「服务器内部意外错误」。
- **Describe 族的 `meta.success`** —— 原来写的是「成员表非空」，把**空**当成了**失败**：
  真实存在但确实没有成员的对象被报成 `success=false`，调用方于是去「修」一个没坏的东西。
  改为 `success=true` + `memberCount`。
- **`ImportBlock` 的读回校验用文件名当块名** —— 把 OB100 的 XML 存成 `OB100.xml` 导进去，
  块在 TIA 里实际叫 `Startup`，于是**一次成功的导入被报成「NOT found after import」**；
  反过来，块若被静默降级（例如变成 OB1），光比名字也可能"对上"，只有块号抓得住。
  改为按 XML 里声明的**块名 + 块号**比对：确知不符时明确失败并写明「工程已被改动」，
  读不回来时返回但以「⚠ 未验证」开头。
- **FB/FC 构建器的「StructuredText 内容不能为空」** —— 报错只报内部形参名
  `structuredTextInnerXml`，而调用方填的是 `$.structuredText.operations`，照着报错改不动。

剩余 3 个嫌疑（`GetOpcUaConfig` / `GetPutGetAccess` / `ProbePlcMonitorOnlineCapabilities`）
返回里带明确的 `ok=false`，自洽，**有意保留**。

### 验证

真机跑在真实工程副本上（S7-1200 + S7-1500，V21）：嫌疑 **10 → 3**；
没连项目时 `GetSoftwareTree` / `CompareSoftwareToOnline` 都明说要先 `Connect`；
路径写错时 `GoOffline` / `Compare` 都失败并列出可用 PLC；
**正确路径但离线时，错误指向在线状态而不是「找不到 PLC」**（这条专门防修过头）。
反向哨兵确认 38 个只读工具用正确路径照旧成功。
工具数不变（221），离线套件 126/126，死引用闸门 PASS。

## [2.7.0] - 2026-09-04 - 删除族、硬件 I/O 地址与插槽，以及一条静默错解析路径的修复

这一版补的是**没有它就干不完活**的那类能力：以前能建块却删不掉块、能加整站却改不了
模块地址、插不进信号板。同时修掉一条硬件路径解析的静默错解析——它比缺功能更危险。

### 新增

- **删除族**（#27）：`DeletePlcBlock` / `DeletePlcTagTable` / `DeletePlcType`。
  三个都默认 `dryRun=true`，预览时给出解析到的完整路径、对象类型和**交叉引用**
  （删之前先看谁在用它）。真删之后**重新读一次**确认对象确实不在了才报成功；
  读不回来就明说「未验证」，不当成功。删除路径**拒绝正则和通配符**——删除只接受
  一个精确路径，不接受"匹配到什么删什么"。默认变量表（`IsDefault=true`）拒删。
- **硬件 I/O 地址**（#33）：`GetDeviceItemIoAddresses` / `SetDeviceItemIoAddress`。
  地址是**引擎原值字节偏移**（`%I2.0` 就是 `startAddress=2`），返回里写明了这点，
  不做任何换算。改地址默认 `dryRun=true`，实写之后必定重新读回比对；地址重叠、
  越界、模块锁定由 TIA 判定并把**具体原因**原样报回来，不会静默成功。
- **机架槽位**（#26）：`GetDevicePlugLocations` / `PlugDeviceItem`。
  槽位号一律来自运行期的 `GetPlugLocations()`，不硬编码。`PlugDeviceItem` 是
  「往已有设备上插子模块」，和 `AddDevice`（新建整站）不是一回事，描述里写清了。
  `dryRun` 走 Openness 真实的可行性预检，不写工程。

### 修复

- **设备项路径打错一段，不再静默解析成它的父级。** 路径解析器在某一段匹配失败时，
  会把窗口往后滑一格重试，最终被全局扫描命中路径中间的某一段并原样返回。实测
  `'S7-1200 station_1/PLC_1/不存在的模块'` 会解析成 `'PLC_1'` 本身：读地址读到
  父级的空清单、报「这个设备项没有 I/O 地址」，**改地址会改到父级头上**，调用方看不出
  任何异常。现在本层认领了该段就不再滑动窗口，被认领后必须把剩余段全部走完；
  设备组嵌套那一路仍允许滑动（那里的滑动是设计意图）。这条影响所有按设备项路径
  取对象的硬件工具，不只新增的这几个。
- **`SetDeviceItemIoAddress` 的负数起始地址在预演阶段就报错。** 原来校验只在实写那一路，
  于是 `dryRun` 对负数一律回答「可以改」，等真写才失败——预演反而成了误导。

### 验证

真机跑在一份真实天车工程副本上（S7-1200 + S7-1500，V21），19/19：
含**实删块 + 独立回读确认块没了**、**实改地址 64→700 + 独立回读 + 还原**、
地址重叠必须失败且地址未变、再删已删掉的块必须报 NotFound。
反向哨兵覆盖：`dryRun` 之后工程一字未改、正确的完整路径与省略站前缀的简写路径
照旧解析、`GetDeviceItemTree` 未被路径解析器的修改改坏。
离线套件 126/126，死引用闸门 PASS。

## [2.5.4] - 2026-09-04 - 一批「看起来成功、其实没做」的缺陷

这一版没有新功能，全是修复。它们有一个共同形状：**失败不会被看见** ——
要么把读不回来的结果当成通过，要么把异常吞掉换成一句错的具体结论，
要么让模型照着描述去调一个根本没注册的工具。调用方（尤其是 AI 客户端）
看到的都是绿灯，于是继续往下走，代价推迟到很后面才炸。

### 修复

- **路径写错不再报「成功，0 个块」。** `GetBlocks` / `GetTypes` 在 PLC 路径解析不到时
  照样返回空列表，工具层于是报成功。对模型来说这是最坏的回答：它会据此认为 PLC 是空的，
  转头去建一堆已经存在的块。现在解析不到就是明确失败，并列出可用的 PLC 路径。
- **块列表遍历炸在半路，不再把残缺结果当完整结果返回。**「少了几个块」比「一个都没有」
  更难发现，因为它看起来完全正常。
- **非法正则不再退化成「没有匹配的块」。** 原来每个块都套一层 try/catch 去跑 Regex，
  模式写错时每个块都被吞掉、最后返回空列表 —— 把「你的模式非法」说成了
  「工程里没有这样的块」。现在当场报错并说清怎么改。
- **按名字取块不再返回错块。** 名字里含正则元字符时走的是不锚定匹配 + 取第一个，
  而西门子块名常带 `.`：请求 `FB_Motor.V2` 会命中 `X_FB_MotorAV2_Old`，
  **错块被以你请求的名字标注返回**，调用方无从察觉；导出、交叉引用都跟着挂到错误对象上。
  现在的次序是：字面精确同名 → 锚定匹配 → 命中多个就报歧义并列出候选。
- **编译结果读不回来不再算作零错误。** `ErrorCount` 是可空的，`null` 专门表示
  「没读回来」。五处写成 `(ErrorCount ?? 0) == 0`，其中包括 `tia compile` 命令
  —— 它会**退 0**，脚本和 CI 会据此认为编译干净。
- **HMI 模板的「执行 JSON 检查」曾是恒真的同义反复。** 检查委托缺省时它退化成
  「前面没报错且有条目」，判据里已经含着「没报错」，永远不可能新增一条错误。
  而走这条空转路径的正是发版前的离线验收闸，以及描述里承诺检查 "execution JSON shape"
  的那个工具。委托现在是必填的，编译期堵死。
- **批量 HMI 绑定不再把「没抛异常」当成绑上了。** 回读调了却把返回值扔掉；
  而 Portal 层会把异常吞成 `success=false` 返回，调用方的 try/catch 永远看不到，
  属性名不被控件接受时 TIA 也只是静默忽略。现在判据只认硬证据，读不回来显式标未验证。
- **设备树遍历出错不再被说成「你路径写错了」。** 遍历中途的异常被吞成「找不到」，
  于是叫用户去改一个本来就对的路径。真因现在跟着原消息一起给出。
- **连接被拒不再制造孤儿 TIA 进程。** 授权/白名单被拒时继续扫下一个候选毫无意义，
  扫空之后会去新起一个无头实例 —— 那次同样会被拒，但进程已经拉起来了，
  在用户机器上空转。
- **工具描述不再点名不存在的工具。** 三个名字（`SetForceTableEntry`、`GetCpuOnlineState`、
  `GetAxisParameters`）在 209 个注册工具里一个都没有。AI 客户端照做只会撞
  tool-not-found，然后自己去找别的路子绕 —— 而其中一个撞上的正是「强制写值」
  这种安全敏感操作。现在如实说明事实与真实存在的替代路径。
- **`SetWatchTableModifyValue` 补上物理安全提示。** 它是会真正驱动物理世界的动作里
  唯一一个描述中没有一句警示的。
- **反射工具族（`GetObjectProperty` / `ListObjectChildren` / `InvokeObject` /
  `DescribeService` / `InvokeService`）补齐 `objectPath` 指引** —— 同族的 `DescribeObject`
  写清了不同对象类型该填什么，这五个漏了，调用方必然要试错一次。
- **清掉一批恒返回 null 的死后备分支**，以及两个「算了没人看、而且算错」的返回值
  （子组递归里写的是覆盖不是累积，最终只反映最后一个子组）。

### 行为变化（升级前请看）

以前**静默成功**的几种情况现在是**明确失败**：PLC 路径写不对、正则写错、
块列表没读全、编译结果读不回来。如果你的脚本依赖「返回空列表 = 没有」，
需要改成处理错误 —— 那个空列表本来就可能是「没查成」，只是分不出来。

### 新增：离线自检套件与 CI

这个仓库此前**没有任何测试工程、没有任何 CI**，上面这些修复因此没有东西盯着。
现在有了 `tools/tiaportal-mcp/tests/TiaMcpServer.Tests`（22 条，纯离线、不连 TIA）
和 `.github/workflows/offline-checks.yml`（回归套件 + 工具描述死引用对拍）。
每组用例都带反向哨兵；「执行 JSON 检查」那组还盯 API 形状 ——
委托一旦被改回可选参数，空转路径立刻复活，而所有传了委托的运行期用例一条都不会红。

### 验证

- **真机**：TIA Portal V21，真实工程副本（101 个块），全程只读。8/8 —— 含 4 条反向哨兵
  （正常列块不受影响、合法正则照常过滤 15/101、全限定真名照常取到、真实编译照常拿回 errorCount）。
- **离线**：22/22，并做过故障注入（把委托改回可选 → 2 条如期变红 → 字节级还原 → 全绿）。
- **未能真机复现、只有反向哨兵覆盖的**：编译结果读不回来那一支（无法让 TIA 造出这个状态）、
  设备树遍历异常那一支（健康工程上造不出）、授权被拒那一支（复现它意味着故意触发一次授权拒绝）。
  这三条的正常路径在上面的真机跑里都验过没被改坏，但**异常分支本身只有代码级依据**，如实标注。

## [2.5.3] - 2026-08-31 - HMI 画面编译（#24）：AI 能自己读 HMI 的编译错误

### 新增

- **`CompileAndDiagnoseHmi`**（[#24](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/issues/24)）
  —— `CompileAndDiagnosePlc` 的 HMI 对应版本。生成完画面/变量之后可以直接编译并拿回
  **结构化的错误与警告**（逐条带 `State` / `Description` / `Path`），模型据此自己改，
  不必再让工程师去博途界面里手工编译、再把报错文本贴回来。

  - **WinCC Unified**：`HmiSoftware` 自身不可编译，实际编译的是**它所属的设备** ——
    这与你在博途界面里编译 HMI 时发生的事情一致。因此**硬件组态的诊断会和画面诊断
    出现在同一份结果里**，这是预期行为，不是串味。
  - **经典屏（精智 / Comfort）**：编译 HMI software 自身。

  工具数 208 → 209；精简档（默认）48 → 49，新工具属于金路径，默认档即可直接调用。

### 说明：这个功能是怎么被验出问题的

第一版实现把「编译目标」判定为「该对象是不是 `IEngineeringServiceProvider`」。
这在 PLC 与经典屏上都能过，**在 WinCC Unified 上却是坏的** —— Openness 里几乎所有对象
都实现该接口，于是代码取到软件的直接父 `DeviceItem` 就收工，而那一层**并不持有**
`ICompilable`；`GetService<T>()` 服务不存在时只返回 `null` 而不抛异常，于是失败发生在
更靠后的地方，报出来是

    HmiSoftware via DeviceItemImpl.GetService<ICompilable>() returned null

正式判据因此改为「**它到底给不给得出 `ICompilable`**」，并沿 owner 链逐层向上查找
（Unified PC 站是 `Device -> DeviceItem -> DeviceItem` 的嵌套，层数不该被写死）。
若一路都找不到，错误信息会把走过的每一层及其结果一并列出。

**发布前的真机验证**（TIA Portal V21，MTP700 Unified Basic 6AV2 123-3GB32-0AW0）：

| 用例 | 结果 |
|---|---|
| Unified 屏（未配置起始画面，必然编译失败） | `State=Error`，`errorCount=3`，三条错误结构化返回（起始画面未配置 / 两条运行系统密码策略） |
| 同工程内 S7-1513 PLC（回归） | `CompileAndDiagnosePlc` → `State=Success`，0 错 0 警 |

先前仅在经典屏上验过 `Success 0/0`，**那只覆盖了 `HmiTarget` 一条分支**，不足以说明
Unified 可用 —— 本版把两条分支分别验过才发布。

### 修复

- **编译诊断不再被静默吞掉**。`CompileAndDiagnosePlc` / `CompileAndDiagnoseHmi` 共用的
  收集逻辑此前整体套在一个 `catch { }` 里。编译诊断是 Openness 代理对象树，代理可能在
  遍历途中失效（切换工程、TIA 界面抢走句柄），集合代理一旦在枚举时抛异常，**已经读到的
  诊断会连同异常一起被丢弃**，而 TIA 报出的 `errorCount` 仍是真实值。调用方看到的是
  `errorCount: 5` 配上空的 `errors: []`，并且无从分辨「收集失败」与「本来就没有明细」。

  现在逐条兜异常：能读到多少返回多少，读取过程中的问题写入 `info`
  （前缀 `[诊断收集不完整]`），并新增 `meta.diagnosticsComplete` 布尔字段供程序判断。

## [2.5.2] - 2026-08-25 - 变量表列不出来（#22）+ 看门狗不再自己拉起博途

### 修复

- **`GetPlcTagTables` 恒返回空列表**（[#22](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP/issues/22)）。
  拿到的对象已经是 `PlcTagTableComposition` 了，代码却又去它身上找一个 `.TagTables` 属性 ——
  找不到，而且非空的属性提示列表还绕开了"直接当集合枚举"的分支，helper 把这一路吞成空列表。
  于是**任何 S7-1200/1500 工程调这个工具都返回 `{"items":[], "success":true}`**，
  看着像"这个 PLC 没有变量表"。与报告人怀疑的 DLL 补丁级别、槽位名 `-7A1` 都无关 ——
  换一台机器、换一个正常命名的 S7-1500 工程照样空。
  已在一个含 7 个 PLC 的真实 S7-1500 工程上验证：修复前每个 PLC 都返回 `[]`，
  修复后各自列出 1～6 张变量表，按名导出得到可用的 XML；
  传入不存在的表名会明确报错并列出可用表名（必错哨兵）。
  分组内变量表的递归遍历与已在用的强制表遍历同构，但该测试工程里没有分组变量表，
  **这一条未经真机验证**。
- **嵌在用户分组里的变量表现在也能列出/导出**。此前只扫根层，分组里的表既列不出也导不出。
  分组内的表以 `分组名/表名` 返回，`ExportPlcTagTable` 两种写法都收（也接受反斜杠，
  方便直接粘 `GetCrossReferences` 打印的路径）。
- **枚举失败不再伪装成"没有变量表"**：拿不到变量表容器时明确报错。
  `ExportPlcTagTable` 也不再把"没这张表"和"找到了但导出被拒"报成同一句话 ——
  前者会把实际可用的表名列出来。

### vci-watch

- **看门狗不再自己拉起博途**。判断"博途开着"此前数的是 `Siemens.Automation.Portal.exe`
  的进程数，可实测**一个开着工程的 GUI 就是 3 个进程**（GUI + HMICompiler +
  SearchReplaceBackgroundProcess），而一台没开任何工程、只剩一个 Openness 无头残留的机器
  同样数到 1。后者最坏：看门狗判定"博途开着"→ 起引擎 → 引擎找不到能附着的工程 →
  自己再拉起一个无头实例 → 600 秒超时被掐断 → 留下的残留让下一轮继续为真。
  实测整夜每 72 分钟空转一次，日志里全是"本轮超过 600 秒，按卡死掐断引擎"。
  现在：带 `-bootstrapper=` 的一律不算会话；起引擎后、`Connect` 之前先用
  `ListPortalProcessProjects`（只探测、不拉起）确认真有工程可搭，没有就直接收工。
- **无头残留会被补杀**。计划任务的 15 分钟上限短于最坏一轮，整个脚本被掐死时
  `finally` 跑不到，无头实例就留着白占内存（实测有一个 88MB 的挂了一整天）。
  现在杀之前先把 PID 记进 `watch.state.json`，下一轮开工时按**完整命令行**核对后补杀 ——
  只认 `Openness.Loader.BootStrapper`，工程师的 GUI 命令行里没有它，天然不会误伤。
- 2.5.1 的退避修复此前只推到了 `v21` 分支，没进 `master`、没打 tag，**公开发布里从未出现过**；
  本版一并带上（内容见下方 2.5.1 条目）。

### 版本一致性

- `scripts/Validate-Bundle.ps1` 新增版本一致性检查：CHANGELOG 首条、`TiaMcpServer.csproj`
  的 `AssemblyVersion`、`manifest/package-manifest.json` 的 `bundleVersion` / `packageName`
  必须一致，对不上直接判失败。此前这几处各说各话（csproj 2.5.0 / manifest 2.5.1 /
  vci-watch README 2.5.1），只能靠人记得改。

## [2.5.1] - 2026-08-23 - vci-watch 退避修复（引擎二进制未变，仍为 2.5.0）

轮询式看门狗的一个真缺陷：**把"稳定的坏状态"当成"一次性故障"反复重试**。

现场表现是"博途界面一直闪，像 MCP 一直在连接"。根因：工程里有 11 个块改了但没编译 ——
这批块每轮都被判 `Unequal`，而导出每轮都因 `The block is inconsistent` 失败，
**永远不会变回 Equal**；有积压时一轮完整周期长达 533 秒（345 次状态查询 + 11 次注定失败的导出重试），
而任务 2 分钟一轮，于是一轮刚结束下一轮就起，几乎一直挂在博途上。

（诊断时先排除了两个更像的嫌疑：博途空闲 60 秒并不写工程目录；跑完一轮后变更信号不变，
也不是看门狗自触发。）

修复：
- **待编译退避** `pendingCompileCooldownMinutes`（默认 30 分钟）：一轮的失败若全是"未编译"，
  冷却期内不再重试；解除条件是**工程目录被动过**（编译会写 `XRef\`），不是干等计时器。
- **最小完整检查间隔** `minFullCheckMinutes`（默认 10 分钟）：避免长周期首尾相接变成常驻连接。
- 建议计划任务间隔从 2 分钟放宽到 **5 分钟**；空闲时每轮 0.37 秒秒退，连引擎都不启动。

决策表七种情形正反两向测试通过（含必错哨兵）：该停时秒退，该跑时照跑
（冷却过期 / 工程被动过 / 超过兜底间隔，三个恢复条件各验一次）。

## [2.5.0] - 2026-08-20 - 把博途工程放进 Git：版本控制接口（VCI）全流程

**这版的主角**：TIA V21 的版本控制接口（Version Control Interface）。博途工程是二进制的，
Git 没法 diff；VCI 把工程映射成一个普通文件夹、每个对象一份文本文件，于是"改了哪个块、改了哪一行"
第一次变得可 diff、可 review、可回滚。本版把整圈做成命令，**不需要在博途界面里点任何东西**。

### 怎么用（三步）

```
1) CreateVersionControlWorkspace(workspaceName="git", folderPath="D:\repos\my-plc")
2) ConnectProjectToWorkspace(dryRun=false)      ← 整工程自动纳管，几百个块一条命令
3) SyncVersionControlWorkspace(direction="ProjectToWorkspace", dryRun=false)
   然后：git add -A && git commit
```

之后每次想知道改了什么：`GetVersionControlStatus(changedOnly=true)` —— 精确到块，
这就是 change log 的输入。完整指南见 **`docs/version-control-git.md`**。

### 新增

- **VCI 五件套进入本包（工具数 203 → 208）**：`CreateVersionControlWorkspace` /
  `ConnectProjectToWorkspace` / `GetVersionControlWorkspaces` / `GetVersionControlStatus` /
  `SyncVersionControlWorkspace`。其中 `ConnectProjectToWorkspace` 是全新工具：递归遍历工程树，逐对象询问
  `GetSupportedFileFormats`，支持的就纳管。**粗粒度优先**（能整体纳管就不往下拆），
  不支持的对象**逐条报出**而非静默丢弃。默认 `dryRun=true`。
- **`tools/vci-watch/`**：约 300 行的看门狗，程序一改一编译就自动导出、写 CHANGELOG、`git commit`。
  只用免费档工具；只读附着、绝不打开工程、绝不写工程、绝不替你编译；无变更时完全静默不产生空提交。

### 分层调整：按“方向”分，不按“工具”分

免费档现在覆盖**完整闭环**——`CreateVersionControlWorkspace`、`ConnectProjectToWorkspace`、
`GetVersionControlWorkspaces`、`GetVersionControlStatus`，以及
`SyncVersionControlWorkspace(direction='ProjectToWorkspace')`（导出）。
这些**只读工程、只写文本**。唯一需要 Pro 的是 `direction='WorkspaceToProject'`
（把 Git 里的版本灌回工程，**会覆盖工程里的块**）。

此前 Create/Connect/Sync 全在 Pro 侧，免费用户能查出"哪些块变了"却建不了工作区、导不出文本 —— 用不起来。

### 修复

- **`GetVersionControlStatus` 返回的是类型名而不是状态**：`MappedObject.GetStatus()` 返回
  `IndividualObjectCompareResult`，直接 `ToString()` 得到类名。改取 `.CompareState`
  （`Equal` / `Unequal` / `WorkspaceFileMissing` / `Unknown`）。此前整列状态都是无意义字符串。
- **同步会对已一致的对象报错**：Openness 拒绝对状态为 `Equal` 的映射调用 `Synchronize`
  （`Synchronize cannot be called on a workspace mapping that has a compare status of equal`），
  在一个 345 对象的工程上会一次报出 345 条失败。现在 `Equal` 一律跳过，并在汇总里报出跳过数。
- **代理对象生命周期硬化**：Openness 的调用**一抛异常就会 dispose 掉相关对象**——问一次
  "工程支不支持纳管"得到"不支持"，`Workspace` 句柄当场作废，后续全报
  `Access to a disposed object`（看着像博途崩了，其实没有）。扫描逻辑现在每次失败后重取句柄。
  另外，通用反射桥 `GetComposition` 返回的是**临时代理，拿到即死**，遍历改为类型化。

### 已知边界（工具会明说，不会假装成功）

- **硬件组态进不了 VCI**（设备/模块/子网一律"不支持"），仍需 `.ap21` 备份或 CAx 导出。
- **专有技术保护的块导不出**：`The block is know-how protected. Export is not possible.`
- **块改动后必须编译才导得出**：`The block is inconsistent. Compile the block prior to export.`
  检测不受影响（未存盘也能检测到），只是导出要等编译。
- **`Unequal` 不等于内容变了**：`git checkout`/`pull` 把文件原样重写（时间戳变）同样判 `Unequal`，
  自动化脚本提交前应再看一眼 `git status` 有没有真差异。

### 验证

真实起重机项目（5 台 PLC、159 MB 工程）：**345 个对象自动纳管**（约 165 秒），
文本仓 345 个 `.xml` / 22 MB；在博途界面里改块并编译 → 看门狗自动导出并提交，diff 就是那处改动；
反向（从 Git 版本还原回工程）→ 编译 0 错 0 警告。MCP 回归 **287/287**，V20/V21 双编译 0 错 0 警告。

## [2.4.1] - 2026-08-20 - 安全修复：不再默默关掉用户打开的工程

**建议所有 v2.4.0 及更早版本用户升级。** 这是一个会真实丢数据的缺陷。

**现象**：让 AI「新建一个工程」或「打开另一个工程」，结果它把你在博途界面里正开着、
正在改的那个工程关掉了，**未保存的编辑一起丢**。

**根因**（两个各自合理的设计叠加）：
1. `Connect` 遍历运行中的 TIA 进程时，**优先接管「已经打开了工程」的那个** —— 这对
   `AttachToOpenProject` 是正确的；
2. `CreateProject` / `OpenProject` 的第一步都是关闭当前工程。

叠加之后，「新建工程」实际执行的是「关掉工程师正在改的工程」。

**修复**：引擎现在区分「这个工程是不是本会话打开的」。检测到是**用户自己打开**的工程时，
`CreateProject` / `OpenProject` **直接拒绝**，并提示两条出路：改用 `AttachToOpenProject`，
或在**征得用户同意后**显式传 `closeForeignProject=true`。

护栏放在工具层最前面 —— 工具原本在委托给引擎之前就已经先关了工程，护栏放在下层来不及。

**验证**：三阶段复现测试。A 引擎建工程并留在 TIA 里后退出；B 引擎全新连接、继承了一个
自己没打开的工程；`CreateProject` 与 `OpenProject` 双双被拒；**对照检查**确认两次拒绝之后
工程仍然开着（证明不是「关完再报错」）；`closeForeignProject=true` 时逃生通道正常。
## [2.4.0] - 2026-08-20 - 精简工具档位成为默认 + FindTools/CallTool 按需取用

实测：全量 203 个工具的 `tools/list` 是 **152 KB / 约 38,800 tokens**，宿主每轮对话都要重发给模型；而 VS Code/GitHub Copilot 的 agent 模式超过 **128** 个工具直接拒绝加载，Windsurf 上限 **100**——也就是说旧的默认档在这两个宿主上根本起不来。

- **默认改为精简档**：`tools/list` 只列 48 个核心工具（33 KB / 约 8,500 tokens，**每轮省约 3 万 tokens**）。此前 lite 只在 `config` 写出的配置里通过环境变量选入，服务端自身默认仍是全量；现在服务端默认就是精简档。
- **新增 `FindTools` + `CallTool`**：精简档不再是死路。模型用大白话搜索（`FindTools("watch table")`）拿到工具名、参数签名和完整说明，再用 `CallTool(name, argumentsJson)` 照常执行——**能力一个不少**，只是不预先塞进上下文。两个桥接工具本身约 700 tokens。
- **模型不用人教**：握手指令和 `Bootstrap` 的 operatingRules 都会说明「你看到的工具列表不是全部，缺什么先跑 FindTools」，所以任何宿主、任何模型拿到就会用。
- **档位开关**：新增 `--profile lite|full` 命令行参数，优先级高于 `TIA_MCP_PROFILE`。默认 lite；拼错的值一律退回 lite，不会静默把宿主顶爆。`tia config` 生成的配置默认**不再写死任何档位**（交给引擎默认），`--full` 才写 `TIA_MCP_PROFILE=full`。
- 错误信息面向「怎么改」：工具名拼错给出候选（含中间打错字的前缀匹配），缺参数直接打印完整签名，`argumentsJson` 非法说明正确形状。

哨兵与回归：
- `scripts/Check-LiteProfile.py` 修掉一处**空跑通过**——它原先用「不设环境变量」来取全量档，默认改为 lite 后两次探测都返回同样的 48 个工具，所有断言形同虚设。现在两档都显式传 `--profile`，并断言 full 严格大于 lite、默认档等于 lite。已用反例验证该哨兵确实会 FAIL。
- `scripts/Validate-Bundle.ps1` 新增引擎桥接门禁（二进制标记扫描，不需要启动引擎）。以 `FindTools` 为标记，因为 `CallTool` 在 MCP SDK 内部字符串里也存在，会让没有桥接的引擎误判通过。
- 新增 `scripts/Generate-ToolsList.py`：从真实 `tools/list`（显式 `--profile full`）重建 `manifest/tools-list.json`，避免清单只记录默认档的四分之一。
- 全部工具逐个冒烟：无崩溃、无服务端掉线。
## [2.3.2] - 2026-08-19 - 装得上·连得通·报得准

本轮全部围绕「新机装完到第一次跑通」这一段，工具能力零新增。

- **doctor 从 3 项扩到 6 项，中文环境输出中文**。新增的三项都是新机最常卡住却此前完全不查的：
  Openness 接口 DLL 能否**真解析**（装了 TIA ≠ 装了 Openness，旧检查只问注册表，于是「体检 OK、
  首次调用 FileLoadException」）、文件被 Windows 标记为网络来源（MOTW，从下载的 zip 解压即中）、
  .NET Framework 4.8。CLI `tia doctor` 与 MCP `Doctor` 工具改为共用 `Runtime/EnvironmentDoctor`。
- **doctor 区分「注册了」与「能用」**：配置从别的机器带过来或交付包换了位置时，条目还在但 exe 已不在，
  宿主只会静默起不来；现在直接报出失效路径并给出修复命令。
- **AI 宿主 4 → 8**：新增 Codex(TOML) / Gemini CLI / Windsurf / Cline。Codex 走 TOML 段落合并并写入
  `startup_timeout_sec = 120`（TIA 启动远超 Codex 默认 10 秒，否则被当成崩溃杀掉）；JSON 写入改为
  临时文件 + 替换的原子写。
- **lite 档补 4 个金路径工具**：`ImportBlocksFromDocuments` / `ExportBlocksAsDocuments` /
  `GetPlcTagTables` / `GetCrossReferences`（42 → 46，仍远低于 VS Code 的 128 上限）。新增
  `scripts/Check-LiteProfile.py` 把「lite 必须能走完金路径」变成可执行断言。
- **Validate-Bundle 增加启动器哨兵**：解析每个 `.cmd`/`.bat` 实际会启动的 exe 并核实存在——此前校验
  脚本与启动器指向不同路径且互不校验。
- **lad 指南补三条**，均以 1052 个真实 `.s7dcl` 语料核过：每个 NETWORK 前必须有独立
  `{ S7_Language := "LAD" }` pragma（10147/10147 无一例外）；`P_Trig(mem)` 一参 vs
  `P_Contact(signal, mem)` 两参；泛型指令模板名随指令而异，报错会列出 `Allowed Template Names`。
- **分支体例补齐**：新增官方 `v21` / `v20` 版本分支（与 v16~v19 同体例）。双版本共用的改动仍进
  `master`，只有版本专属适配才进版本分支。
### 同批合入：DownloadToPlc 多网卡选错 PG/PC 接口（issue #14）

`DownloadToPlc` 在多网卡机器上报「连接到模块 PLC_1 失败」——WLAN / VPN 虚拟网卡排在 PLCSIM 虚拟网卡前面，而 `ApplyConfiguration` 对物理网卡也返回成功（它不校验可达性），于是下载从一块根本看不见 CPU 的网卡走出去。

- **按 IP 排序选路（自动修）**：`Modes → PcInterfaces → TargetInterfaces` 不再「取第一个能 Apply 的」，而是把全部路由枚举出来打分——**PG/PC 网卡自身 IP 与目标 CPU IP 同一 IPv4 /24 的优先**。真机场景（WLAN 192.168.31.x + VPN 198.18.x.x + PLCSIM 192.168.0.241 → CPU 192.168.0.1）自动选中 PLCSIM 网卡。
  - 注意：Openness 的 `ConfigurationAddress` 只给地址不给掩码，**/24 是假设**；它只用来给候选**排序**，从不用来否决下载。所有候选同分（例如 PROFIBUS/MPI、或没有网卡在 CPU 网段）时**保持原枚举顺序**，即旧的 first-wins 行为不变。
- **可显式指定（手动兜底）**：`DownloadToPlc` 新增两个可选入参——`pgPcInterface`（网卡名子串，不区分大小写，如 `PLCSIM` / `Realtek`）与 `targetIpAddress`（目标 CPU IP，如 `192.168.0.1`）。填了却匹配不到，直接报错并**列出全部可用路由**，不会悄悄回落到错的网卡。
- **可诊断**：`CheckDownloadReadiness` 的 `meta.downloadRoutes` **只读**列出每条「PG/PC 网卡 → CPU 接口」路由及两端 IP（按同一套排序，`preferred=true` 表示同网段），下载前就能看出选路是否合理；`DownloadToPlc` 成功时 `meta.pgPcRoute` 回报实际走的网卡，失败时错误信息附上已用路由 + 全部候选 + 覆盖参数的用法。
- **验证**：新增 `scripts\Test-DownloadRouteSelection.ps1`——离线、不需要 TIA 连接，用伪造的多网卡对象图跑真实选路代码，覆盖自动选中 PLCSIM / 显式 IP / 显式网卡名 / 两种匹配失败的报错 / 同分回落原顺序，外加一条**必错哨兵**（若哨兵通过说明测试本身坏了）。7 项全过。V21 编译 0 错。
## [2.3.1] - 2026-07-25 - 门槛收敛：git clone 开箱即用 + 精简档成为一键配置默认 + lite 补齐金路径工具

围绕「公开版 = 门槛低、好用能用实用」的定位收敛（工具能力零新增，全部是让现有能力更容易被用对）：

- **git clone 用户开箱即用**：全部启动脚本（`配置MCP*.bat` / `tia*.cmd` / `scripts\生成工程.bat` / `预热.bat`）增加双布局自动回退——先找交付 zip 布局 `tools\...\bin[-v20]\Release\net48`，找不到自动改用 git 布局 `runtime\v21`。此前 git clone 后双击任何脚本都报「找不到引擎 exe」（bat 指向被 .gitignore 排除的 bin 目录）。`cursor-mcp.example.json` 同步修正为真实存在的路径并注明两种布局。
- **`tia config` 默认写精简档（行为变更）**：一键配置默认带 `TIA_MCP_PROFILE=lite`（约 42 个核心工具）——弱模型不再被 200+ 工具淹没，VS Code 128 工具上限不再爆；要全量工具面显式 `config --full`（`--lite` 仍接受，向后兼容）。服务器侧默认不变：不带环境变量启动仍是全量。
- **lite 档补齐金路径工具（修 bug）**：lite 此前按描述前缀 `[L0]/[L1]` 过滤，ServerInstructions 金路径主推的 `ImportFromDocuments` / `GenerateBlocksFromExternalSource` / `GetBlocks` / `GetBlocksWithHierarchy` / `GetBlockInfo` / `ExportAsDocuments` / `GoOffline` 却是 L2——弱模型在 lite 档被说明书指去调这些工具时根本看不见。现改为**显式工具名白名单**（42 个，成员不再随描述文案变动漂移），上述 7 个金路径工具全部纳入。
- **文档一致性**：README 标题不再硬编码版本号（此前停在 v2.2.8 与 Release 脱节）；新增「两种获取方式 exe 路径对照表」；`docs/README.md` 新增按角色导航的文档单一入口；`doctor` 提前为「装完先体检」第一步；`使用说明与介绍` / `CLI_quickstart` 的 exe 路径口径与 README 对齐。

## [2.3.0] - 2026-07-04 - 新工具 DescribeBlockLogic：把梯形图读成可读逻辑（含"恒断/禁用行"自动标注）

回应"分析慢/准确率低/只敢用 SCL 躲梯形图"的痛点——加一个让模型/人像读 SCL 一样读 LAD 的工具：

- **新工具 `DescribeBlockLogic(softwarePath, blockPath)`**［L1，lite 档也含］：导出块后把每个 LAD 网络**重建成可读的能流表达式**——串联触点用 `·`、并联用 `+`、常闭显示 `/操作数`；线圈 `( )/(S)/(R)`、MOVE/比较/定时器盒带管脚操作数；SCL/STL 网络内联成代码。**关键**：自动标注接到字面常量的触点——`⟨恒断·禁用本行⟩`(常开触点接 FALSE=永久断开、静默禁用整行，肉眼几乎看不出)、`⟨恒通⟩`(恒导通旁路)。比导出 FlgNet XML 手工追线又快又准。
  - 实证：对 5T车 原生 `Auto_SpeedSet`(LAD+SCL+STL 混编)一眼标出"初始化切换"里被恒 FALSE 触点禁用的 `0006/0007` 握手行(`TP(..).Q · 0 ⟨恒断·禁用本行⟩`)——正是之前手工追半天才发现的坑；复位行 `MOVE 16#04FE` 无禁用标记=有效。端到端(测试PLC/FC_Alarm)输出 `"DemoData.HighAlarm" ( ) ⇐ ("DemoData.Temp" >= "DemoData.Limit")`。
  - ServerInstructions 与 GetAuthoringGuide('lad') 补指引：**读 LAD 用 DescribeBlockLogic，别手解 XML**。
- 纯只读、Siemens-free 渲染器(`LadTextRenderer`)，工具数 200→201。V20/V21 双编译 0 错，渲染器离线对真实块 + 端到端各验证一遍。

## [2.2.9] - 2026-07-04 - 文档导入健壮化：嵌套组不再失败/丢块号 + 真实错误上抛 + 导出失败原因可见

真机排障中暴露的 SIMATIC SD 文档导入/导出缺陷，均在活工程(5T车)+测试PLC 实测修复：

- **`ImportFromDocuments` 嵌套组导入失败 + 块被挪到根/重编号（真机咬到的 bug）**：
  - 旧行为：带非根 `groupPath`（如 `01_手动控制/FB控制`）导入时，一旦 Openness 抛异常就被外层 catch **吞掉、只返回 false**，工具层只能报泛化的 `Failed importing X`；而 group 解析为 null 时会**静默改导到根组**，导致块从原分组挪到根、`AutoNumber` 把 FB21 改成 FB4。
  - 新行为：① 组路径非空但解析不到 → 明确报错「Group path 'X' not found，用 GetSoftwareTree 查准确组名」，**绝不静默转根**；② 导入异常按类型上抛真实原因（`.s7dcl` 语法/类型、`.s7res` 与 S7_MLC 不匹配等）；③ 导入前记录原块号，Override 若把符号块重编号则**自动改回原号**，保持工程树与实例 DB 稳定。实测：嵌套组 Override 成功、块留原组、FB 号保号。
- **`ExportBlocksAsDocuments` 静默返回 0 → 现在报出每个块的失败原因**：旧版把失败块收进内部 `failures` 列表却不返回，`totalBlocks>0 但 exportedBlocks=0` 看着像成功。现在响应 `meta` 带 `skippedBlocks`/`failures[]`，消息也写明。实测挖出真因：**含 STL 网络的块无法导出 SIMATIC SD**（Auto_SpeedSet 就是），提示改用 ExportBlock(XML)。
- **验证**：V20/V21 双编译 0 错；bin-build exe 作第二 Openness 客户端连活工程实测 4 项全过（组不存在报错/嵌套组 Override 保号/导出失败原因可见/测试PLC 11 块全导出 0 误报）。

## [2.2.8] - 2026-07-03 - 弱模型档位回归 + Doctor 体检 + 写操作默认干跑 + VS Code schema 修复

围绕「外部环境部署 + 弱 AI + 出错率」（lite 档位与 Doctor 此前只存在于 SKILL 文档、代码在迁仓时丢失，本版补齐并超越原实现）：

- **lite 工具档位（弱模型/受限宿主）**：`TIA_MCP_PROFILE=lite` 环境变量生效——`tools/list` 只暴露 [L0]/[L1] 共 42 个核心工具（full=200），弱模型不再被 200 个工具淹没，VS Code 的 128 工具上限也不再爆。一键写入：`tia config --lite`（宿主配置里自动带 `env`），GUI 配置向导同步提供勾选。stdio 实测 full=200 / lite=42、核心工具（Bootstrap/Connect/GetProjectTree/ScaffoldProject/CompileSoftware/SaveProject/Doctor）全在。
- **`Doctor` 工具回归 + 新 CLI `tia doctor`**：MCP 工具版一次体检 TIA 安装/Openness 用户组/连接与项目状态，逐项给出精确修法，`fix=true` 自动补 Openness 组；**CLI 版 `tia doctor [--fix]` 在 MCP 宿主根本起不来 server 的时候也能用**（正是最需要体检的场景），额外检查 exe 编译版本与本机 TIA 版本匹配、四宿主配置是否已注册。stdio 调用与 CLI 实测输出正确。
- **启动失败不再无声**：TIA 未装（Openness 初始化 FileNotFound）与用户不在 Openness 组两条失败路径，stderr/日志给出中英文修复指引（指向 `tia doctor`），后者退出码从 0 改为 2，MCP 宿主能感知失败。
- **写操作默认干跑（约束加强）**：`ScaffoldProject` 的 `dryRun` 默认值 false→**true**——默认调用只做离线校验、不连 TIA 不建工程，干跑干净后需显式 `dryRun=false` 才真跑；干跑结果消息里明确指引下一步。与 ServerInstructions「永远先 dryRun」铁律一致，弱模型一把梭建废工程的路径被堵死。（行为变更：依赖旧默认值的调用方需显式传 `dryRun=false`。）
- **VS Code 工具校验修复随本版 exe 发布**（源码 38043e9 已在 master）：`InvokeObject`/`InvokeService` 数组入参 schema 补 `items`，VS Code 不再拒收。
- **配置脚本加固**：`配置MCP.bat`/`配置MCP-v20.bat` 先检查引擎 exe 存在（防"只拷了 bat"），完成后提示 `--lite`/`doctor`/`--print` 三条路。
- 验证：V20/V21 双编译 0 错；stdio 握手实测（full/lite 工具数、Doctor 诊断内容、instructions 下发）；`tia doctor`/`tia config --print --lite` CLI 实测。

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

