# TIA VCI 看门狗

博途里的程序一变，就自动导出成文本、写 change log、git 提交。**不需要任何手动操作。**

## 它做什么 / 不做什么

**做**：
- 每轮：博途在跑吗 → 附着到你已经打开的工程 → 问 VCI「哪些块和文本文件不一致」→
  有变更就导出（`ProjectToWorkspace`）→ 写 `CHANGELOG.md` → `git commit`。
- 没变更 → **一声不吭**，不产生空提交。

**绝不做**（改代码前先读这三条）：
1. **绝不打开博途、绝不打开工程**。博途没开就直接退出。你的工程只能 Attach —— 这是红线。
2. **绝不往工程里写**。只做 `ProjectToWorkspace` 一个方向，永远不调 `WorkspaceToProject`，
   也不调 `SaveProject`。存不存盘是你的决定。
3. **绝不替你编译**（除非你在 config 里显式打开 `autoCompile`）。

## 一个必须知道的限制：改完要编译，才导得出

西门子的规矩：块改动后处于 inconsistent 状态，VCI 拒绝导出 ——
`The block is inconsistent. Compile the block prior to export.`

所以：
- **检测**：改完立刻就能检测到（**哪怕你还没存盘**，实测确认）。
- **导出/提交**：要等这个块被编译过。你在博途里编译一次，下一轮看门狗就自动补上。
- 想省这一步 → config 里 `"autoCompile": true` 并列出 `compileSoftwarePaths`，
  它会自己编译再导出（注意：编译是写操作，会改工程状态）。

另一个限制：**专有技术保护（know-how protected）的块导不出**，
VCI 明确拒绝（真实项目里遇到过一个这样的 FB），这类块进不了版本管理。
**硬件组态也进不了 VCI**，只有程序侧（块 / 变量表 / UDT）。

## 配置 `config.json`

```json
{
  "enginePath": "E:\\PID博途块\\MCP\\_bulaofen_release\\runtime\\v21\\TiaMcpServer.exe",
  "tiaMajorVersion": 21,
  "workspaceFolder": "C:\\path\\to\\your-git-worktree",
  "workspaceName": "git",
  "gitAuthor": "tia-vci-watch <watch@local>",
  "autoCompile": false,
  "compileSoftwarePaths": ["PLC_1"]
}
```

`workspaceFolder` 必须同时是 **VCI 工作区**和 **git 工作树**。
工程侧要先做过一次 `ConnectProjectToWorkspace`（整工程自动纳管），看门狗才有东西可看。

## 跑

- 手动跑一轮：`python watch.py`
- 定时跑：`register-task.ps1`（注册 Windows 计划任务，默认 10 分钟一轮），
  卸载：`register-task.ps1 -Remove`
- 日志：`log\watch-YYYYMMDD.log`（每轮重算文件名，跨天不会堆进同一个文件）

## 已验证（真实项目，345 个对象）

- 无变更 → 不动作、不提交（反向哨兵，实测通过）
- 工程侧改了**且未保存** → 检测到
- **✅ 在博途 UI 里手改并编译 → 全自动检测、导出、写 CHANGELOG、git 提交**
  （现场验证：在博途里给某个块加了一句行注释并编译，看门狗自动导出并提交）
- 改了但未编译 → 只记「待编译」，**不误报失败、不提交**
- 编译后 → 自动导出 + 写 CHANGELOG + 提交，diff 里是真实的块内容变化
- VCI 的 `Unequal` **不等于内容变了**：git checkout/pull 把文件原样重写（时间戳变）也会判 Unequal，
  所以提交前还要看 `git status` 有没有真差异，否则会刷空提交（这个坑实测踩到过）


## 资源管控（不许拖慢本机）

实测一轮开销（博途界面开着一个 345 个对象的工程时）：
**无变更约 81~95 秒；有变更（含导出+提交）实测 273 秒**。引擎峰值内存约 72 MB，跑完进程数回到原样。
所以 10 分钟一轮不会叠加，但间隔别设到 5 分钟以下。
耗时几乎全在状态检查——345 个对象逐个问 Openness；同一操作在无头实例只要 3~10 秒，
GUI 实例慢是因为要和界面抢同一个引擎。

为此做了六道闸：
1. **博途没在跑 → 直接退出**，绝不为了检查而拉起博途。
2. **引擎降到 BelowNormal 优先级**，不跟你正在操作的博途抢 CPU；计划任务本身也设为低优先级(7)。
3. **单实例锁**：上一轮没跑完就跳过这一轮，绝不叠加（10 分钟一轮 vs 一轮 81 秒）。
   锁超过 `lockTimeoutSeconds`(默认 900s) 视为卡死，自动接管。
4. **硬超时** `cycleTimeoutSeconds`(默认 **600s**)：守护线程到点**真的杀引擎**，不是只记一行日志。
   已用故障注入验过（把超时调到 5 秒 → 引擎被掐断、退出码 1、无残留）。计划任务另有 15 分钟上限兜底。
5. **残留自清**：把自己引擎的 PID 记在 `watch.state.json`，下一轮开头核对命令行后清掉上一轮卡死的。
   **只认自己记下的 PID，绝不按进程名批量杀** —— 同一个 exe 别的 Claude 会话也在用。
6. **绝不留孤儿博途**：收尾时杀掉「父进程是本轮引擎」的博途实例；
   你自己开的 GUI（父进程是 explorer）绝对不碰。

暂停/恢复/卸载：
```powershell
Disable-ScheduledTask TiaVciWatch      # 临时停
Enable-ScheduledTask  TiaVciWatch
.\register-task.ps1 -Remove            # 彻底卸载
.\register-task.ps1 -IntervalMinutes 30  # 改间隔
```


## 退避：为什么它不会一直连着博途（2.5.1）

轮询式自动化最容易犯的错，是把**稳定的坏状态**当成**一次性故障**去重试。

真实踩到的案例：工程里有 11 个块改了但没编译。这批块每轮都被判「变了」，
而导出每轮都因 `The block is inconsistent` 失败 —— **重试多少次都不会变好**。
结果是每一轮都跑完整周期（有积压时一轮长达 **533 秒**：345 次状态查询 + 11 次注定失败的导出），
而任务 2 分钟一轮，于是一轮刚完下一轮就起，看门狗几乎一直挂在博途上，工程师看到界面持续闪烁。

现在有三道闸：

| 闸 | 参数 | 作用 |
|---|---|---|
| 待编译退避 | `pendingCompileCooldownMinutes`（默认 30） | 一轮的失败若**全是**"未编译"，冷却期内不再重试。解除条件是**工程目录被动过**（编译会写 `XRef\`），不是干等计时器 |
| 最小完整检查间隔 | `minFullCheckMinutes`（默认 10） | 有变更的周期可能好几分钟，短间隔会让周期首尾相接 = 常驻连接 |
| 兜底全量 | `forceFullCheckMinutes`（默认 60） | 即使信号没变，也定期全量查一次，不把启发式当唯一依据 |

空闲时每轮 **0.37 秒**秒退（连引擎都不启动）。建议计划任务间隔 **5 分钟**。

> 一句话原则：**凡是"重试不可能改变结果"的失败，必须退避，且退避的解除条件要挂在外部状态变化上。**


## 它怎么判断"博途开着"（2.5.2）

**不是数 `Siemens.Automation.Portal.exe` 的进程数。** 一台机器上同时可能跑三种：

| 进程 | 命令行特征 | 算不算"你开着博途" |
|---|---|---|
| 你双击 `.apXX` 开的 GUI | 只有工程路径 | ✅ 算 |
| 那个 GUI 自己拉起的后台辅助进程 | `-bootstrapper=…BackgroundProcessBootStrapper` + `-processId=<GUI 的 PID>` | ❌ 不算 |
| 别的 Openness 客户端拉起的无头实例 | `-bootstrapper=…Openness.Loader.BootStrapper` | ❌ 不算 |

实测**一个开着工程的 GUI 就是 3 个进程**。按进程数判断会踩两个坑，第二个是真事故：

1. 高估 —— 明明只开了一个工程，却以为开了三个。
2. **自维持空转** —— 一台没开任何工程、只剩一个无头残留的机器同样数到 1。
   看门狗判定"博途开着"→ 起引擎 → 引擎找不到能附着的工程 → **自己再拉起一个无头实例** →
   600 秒超时被掐断 → 留下的残留让下一轮继续数到 1。实测整夜每 72 分钟空转一次，
   日志里全是「本轮超过 600 秒，按卡死掐断引擎」。

所以现在是两道闸：

- **起引擎之前**：带 `-bootstrapper=` 的一律不算，只数真 GUI 会话。
- **`Connect` 之前**：用 `ListPortalProcessProjects`（只探测已在跑的进程，自己不会拉起博途）
  确认真有工程可搭；没有就直接收工。`Connect` 是会拉起博途的，别让它去"帮忙"。

**残留补杀**：计划任务的 15 分钟上限短于最坏一轮，整个脚本被掐死时收尾代码跑不到，
无头实例就留着白占内存（实测有一个挂了一整天）。现在杀之前先把 PID 记进
`watch.state.json`，下一轮开工时按**完整命令行**核对后补杀 —— 只认
`Openness.Loader.BootStrapper`，你的 GUI 命令行里没有它，天然不会误伤。
