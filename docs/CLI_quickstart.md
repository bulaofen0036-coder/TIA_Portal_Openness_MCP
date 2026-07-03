# tia 命令行 —— 5 分钟上手

v2.0 起，交付包里的 **同一个 exe** 既是 MCP 服务，也是命令行 `tia`。
不需要装 MCP 客户端、不需要会编程：**任意 AI 写一份 spec，你跑一条命令。**

## 让 `tia` 命令可用（一次性）

交付包根目录放了现成入口：

- **`tia.cmd`**（V21）/ **`tia-v20.cmd`**（V20）—— 按你装的 TIA 大版本选一个。

两种用法：

1. **直接用全名**：在交付包根目录下 `tia gen spec.yaml`（V20 用户 `tia-v20 gen spec.yaml`）。
2. **加进 PATH 后随处可用**（推荐）：把交付包根目录加入系统环境变量 `PATH`，之后任意目录都能 `tia gen ...`。

> 下文一律写 `tia`；V20 用户把它换成 `tia-v20`。两个 `.cmd` 只是把参数透传给深层的
> `…\bin\Release\net48\TiaMcpServer.exe`（V21）/ `…\bin-v20\…`（V20），退出码原样返回。
> 不想配也行——直接调那个 exe 全路径效果完全一样。

---

## 三种用法，门槛从低到高

### 1. 最简单：双击 .bat
- 把一个 `spec.yaml` 或 `spec.json` **拖到 `scripts\生成工程.bat` 上** → 自动建工程。
- 想让连接更快：先双击 `scripts\预热.bat`（留着别关），之后每次建工程约 1 秒连上。

### 2. 一条命令
```
tia gen  项目.yaml              # 从 spec 建完整工程
tia gen  项目.yaml --dry-run    # 只离线校验 spec，不连 TIA、不建任何东西
tia patch 改动.yaml             # 把 spec 增量合并进已有工程（spec 里写 projectPath）
tia compile  D:\proj\X.ap21 --plc PLC_1
tia describe D:\proj\X.ap21 --plc PLC_1
tia prewarm                     # 常驻 headless 实例，后续命令 ~1s 连上
tia doctor                      # 一键体检：TIA 安装/exe 版本匹配/Openness 组/宿主注册（--fix 自动修用户组）
tia config                      # 一键把 MCP 注册进 Claude Desktop/Claude Code/Cursor/VS Code（--lite=精简 42 工具档）
tia schema                      # 打印 spec 所有字段说明
```
退出码：**0=成功，1=有失败步骤，2=错误**（方便脚本/CI 判读）。
加 `--json` 输出机器可读结果，方便让 AI 读结果自我纠错。

### 3. 让 AI 生成 spec
把 `docs/AI_spec_prompt.md` 里的提示词 + `tia schema` 的输出贴给任意 AI，
描述你要的工程，AI 产出 `spec.yaml`，再走用法 1 或 2。

---

## spec 长什么样

最小例子（YAML）：
```yaml
projectName: MyLine
plcName: PLC_1
plcFamily: S7-1500
udt:
  - name: UDT_Status
    members:
      - { name: Active, datatype: Bool, commentZhCn: 运行 }
tagTable:
  - tableName: IO
    tags:
      - { name: Start, dataTypeName: Bool, logicalAddress: "%I0.0" }
compile: true
save: true
```
完整字段见 `tia schema`，现成模板见 `templates/project-blueprints/`（启停、电机，均编译 0 错）。
这两个模板**开箱即用**——里面引用 `.scl`/`.s7dcl` 的路径写成 `__BUNDLE__\...`，
`tia` 会自动把 `__BUNDLE__` 解析成交付包根目录，你直接 `tia gen scaffold_spec_motor.json` 即可，
无需手动替换路径。（拷到别处用同样有效，只要 `tia` 还在交付包里。）

提示：
- `tia gen` 从零建；`tia patch` 改已有工程（spec 里加 `projectPath: D:\...\X.ap21`）。
- HMI 画面 `width/height` 要按面板原生分辨率，否则被裁剪。
- `hmiTags` 用绝对地址（`%M..`）更容易通过回读校验。
- JSON 是首选格式（零歧义，AI 生成最稳）；YAML 是给人读写的便利。

---

## 常见问题

- **慢？** 第一次连接冷启动 headless TIA 约 10–28s；先 `tia prewarm` 之后约 1s。再快不了——`CreateProject/AddDevice/Save` 是 Openness 固有耗时。
- **中文乱码？** 输出已强制 UTF-8；`.scl` 文件请存 UTF-8 BOM。
- **V20 还是 V21？** 用与你 TIA 大版本匹配的那个 exe（`bin\Release`=V21，`bin-v20\Release`=V20）。可加 `--tia-major-version 20|21` 或 `--tia-portal-location <安装根>` 覆盖。
- **要看 GUI？** 加 `--with-ui` 用完整界面启动（较慢）。
- **工程路径可以写相对的吗？** 可以——`tia describe/compile/export/import` 和 `tia patch` 的工程路径
  现在按你当前所在目录解析（v2.0 修复，之前只认 exe 目录会报 `Projects.Open failed`）。
- **`tia prewarm` 怎么停？** 在它运行的那个窗口按 `Ctrl+C` 即可优雅关闭。若是后台/双击启动的，
  `tia prewarm --stop` 只会关掉那个 headless TIA 实例，预热**进程本身**需手动结束（任务管理器里的 `TiaMcpServer.exe`）。
