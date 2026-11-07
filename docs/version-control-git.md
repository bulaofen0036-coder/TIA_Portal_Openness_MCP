# 把博途工程放进 Git —— 版本控制接口（VCI）使用指南

> 适用：TIA Portal **V21 及以上**（VCI 是 V21 引入的能力）。V20 及以下没有这套接口。

博途工程是二进制的，Git 没法 diff，所以长期以来"版本管理"只能靠**另存为一堆日期文件夹**。
V21 的版本控制接口（Version Control Interface）解决了这件事：**工作区**就是一个普通文件夹，
里面每个对象一份文本文件（`.xml` / SimaticML），可 diff、可 commit、可 review。

本 MCP 把整圈做成了几条命令，**全程不需要在博途界面里点任何东西**。

---

## 30 秒上手

对 AI 说：

```
把当前工程放进 Git，工作区用 D:\repos\my-plc
```

AI 会依次调用：

```
CreateVersionControlWorkspace(workspaceName="git", folderPath="D:\repos\my-plc")
ConnectProjectToWorkspace(dryRun=false)          ← 整个工程自动纳管，一个块都不用手点
SyncVersionControlWorkspace(direction="ProjectToWorkspace", dryRun=false)
```

然后在那个文件夹里：

```bash
git init && git add -A && git commit -m "PLC 程序基线"
```

**完事。** 之后每次程序改了，问一句"哪些块变了"即可：

```
GetVersionControlStatus(changedOnly=true)
→ A3_4_Hoist | Unequal | ...
```

导出并提交：

```
SyncVersionControlWorkspace(direction="ProjectToWorkspace", dryRun=false)
git add -A && git commit -m "修起升速度分发"
```

---

## 五个工具

| 工具 | 干什么 | 档位 |
|---|---|---|
| `CreateVersionControlWorkspace` | 建工作区，指向一个文件夹（**建议就用 Git 工作树**） | 免费 |
| `ConnectProjectToWorkspace` | **整工程自动纳管**：遍历工程树，把所有支持的对象纳入版本管理 | 免费 |
| `GetVersionControlWorkspaces` | 列出工作区（名字、磁盘路径、已纳管对象数） | 免费 |
| `GetVersionControlStatus` | **逐对象比对**：哪些块与文本文件不一致 —— 这就是 change log 的输入 | 免费 |
| `SyncVersionControlWorkspace` `direction=ProjectToWorkspace` | 工程 → 文本（**导出，提交前跑**） | 免费 |
| `SyncVersionControlWorkspace` `direction=WorkspaceToProject` | 文本 → 工程（**还原**，会覆盖工程里的块） | Pro |

分层的逻辑很简单：**只读工程、只写文本的全部免费**；唯一会**改你工程**的操作（把某个 Git 版本灌回工程）需要 Pro。

写操作默认 `dryRun=true`，先告诉你会做什么，确认后再传 `dryRun=false`。

---

## 覆盖范围：能管什么、不能管什么

✅ **能**：FC / FB / OB / DB、PLC 变量表、PLC 数据类型（UDT） —— 即**整个程序侧**。

❌ **不能**：
- **硬件组态**（设备、模块、子网）—— VCI 不支持，`GetSupportedFileFormats` 直接返回"不支持"。
  硬件仍需 `.ap21` 备份，或用 CAx/AML 导出。
- **专有技术保护（know-how protected）的块** —— 博途拒绝导出：
  `The block is know-how protected. Export is not possible.`
  这类块会在纳管结果里被明确列出，不会被静默跳过。

`ConnectProjectToWorkspace` 采用**粗粒度优先**：能整体纳管的对象就整体纳管，不再往下拆；
不支持的对象**逐条报出来**，绝不静默丢弃。

---

## 三个必须知道的行为（不知道会以为工具坏了）

### 1. 改完要**编译**，才导得出

块改动后处于"不一致"状态，博途拒绝导出：

```
The block is inconsistent. Compile the block prior to export.
```

- **检测**不受影响：改完立刻就能看出这个块变了，**哪怕还没存盘**。
- **导出**必须等编译。在博途里编译一次（或调 `CompileSoftware`），再同步即可。

### 2. `Unequal` 不等于"内容真的变了"

比对不是纯内容比对。`git checkout` / `git pull` 把文件原样重写一遍（内容相同、时间戳变），
同样会被判成 `Unequal`。**自动化脚本在提交前应再看一眼 `git status` 有没有真差异**，
否则会刷出一串空提交。

### 3. 已经同步过的对象不能再"强制同步"

对状态为 `Equal` 的映射调用同步，博途会直接拒绝：

```
Synchronize cannot be called on a workspace mapping that has a compare status of equal.
```

所以本工具**始终跳过 Equal 的对象**，并在结果里报出跳过数量。

---

## 工作区的 Git 侧建议

导出文件是 **UTF-8 with BOM + CRLF**，中文块名很常见。建 `.gitattributes`：

```
*.xml   text eol=crlf working-tree-encoding=UTF-8
*.s7dcl text eol=crlf working-tree-encoding=UTF-8
*.s7res text eol=crlf working-tree-encoding=UTF-8
```

再配 `git config core.quotepath false`，`git log --stat` 里的中文块名才不会变成八进制转义。

---

## 想全自动？看 `tools/vci-watch/`

仓库里附了一个小看门狗：**程序一改一编译，自动导出、写 CHANGELOG、`git commit`**，
工程师什么都不用做。它只用免费档的工具，代码不到 300 行，可以直接抄去改。
详见 [`tools/vci-watch/README.md`](../tools/vci-watch/README.md)。

---

## 一次真实规模的参考数据

某台起重机项目（5 台 PLC、159 MB 工程）：

| 动作 | 数量 / 耗时 |
|---|---|
| 整工程自动纳管 | **345 个对象**，无头实例约 165 秒（博途界面开着时约 265 秒） |
| 后续状态检查 | 3～10 秒（界面开着时约 80 秒） |
| 单块导出 | 约 2 秒 |
| 文本仓体积 | 345 个 `.xml`，约 22 MB |

纳管失败 2 个，原因都被明确报出：一个块不一致（需先编译），一个是专有技术保护块。
