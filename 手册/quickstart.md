# Quick Start — TIA Portal MCP（交付包版）

本文件与**根目录英文/中文 `README.md`** 一致：交付包已包含 **`TiaMcpServer.exe`**，一般**无需**自行 `dotnet build`。若你要二次开发服务端，才需要从西门子提供的源码工程编译（不在本包步骤内）。

---

## 1. Prerequisites

- **Windows**，**.NET Framework 4.8**
- **TIA Portal**（推荐 **V21**；其它主版本视环境与 PublicAPI 而定）
- 用户属于 **`Siemens TIA Openness`**：`whoami /groups | findstr Openness`
- 用户环境变量 **`TiaPortalLocation`** = Portal 安装根，例如：  
  `D:\app\TIA21\Portal V21` 或 `C:\Program Files\Siemens\Automation\Portal V21`
- 首次连接时在 TIA 内允许 **Openness** 访问

---

## 2. Bundle sanity check（offline）

From the **delivery package root** (folder containing `README.md`):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate-Bundle.ps1
```

---

## 3. Wire MCP — **stdio recommended**

Copy `cursor-mcp.example.json` into your client config. Set `command` to the **absolute path** of:

`tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe`

Restart the client. First automation calls: **`Bootstrap`** → **`Connect`** → **`GetProjectTree`**.

**Static docs shipped in this bundle:**

- `manifest/tools-list.json` — tool names / layers  
- `docs/tool-capability-matrix.md` — capability matrix  

When the server is running, **`tools/list`** (or your client’s tool picker) is the **authoritative** runtime roster — counts may drift slightly across builds.

### V17 PLC-Software quick path

- Build:

```powershell
dotnet build .\tools\tiaportal-mcp\src\TiaMcpServer\TiaMcpServer.V17.csproj -c Release
```

- MCP runtime:

```text
tools\tiaportal-mcp\src\TiaMcpServer\bin-v17\Release\net48\TiaMcpServer.exe
```

- Required runtime profile:

```text
--tool-profile plc-software-v17-phase1
```

- You can also set:

```text
TIA_MCP_TOOL_PROFILE=plc-software-v17-phase1
```

- Current V17 scope:
  - project
  - hardware
  - PLC build/import
  - compile/save
  - readback

- Current V17 gated / not included:
  - V20+ Documents / S7DCL
  - Unified HMI
  - automatic download
  - online write
  - force

- Suggested local validation for V17:
  1. build `TiaMcpServer.V17.csproj`
  2. run `tools/list`
  3. run a real temporary TIA V17 PLC smoke project

- Environment note:
  - on some V17 machines, `Connect` may hang while Openness cold-starts `TiaPortal`
  - this branch adds attach timeout, launch timeout, and orphan portal cleanup to make the failure mode explicit
  - if this occurs, close leftover `Siemens.Automation.Portal.exe`, retry `Connect`, or open TIA manually first and then use `AttachToOpenProject`

---

## 4. HTTP transport（advanced）

```powershell
TiaMcpServer.exe --transport http --http-prefix http://127.0.0.1:8765/ --http-api-key <secret>
```

- **`GET /mcp/health`** — liveness only  
- **`POST /mcp`** — full **MCP JSON-RPC session** (not a single bare `tools/call`). Custom scripts must implement the protocol (initialize, session/SSE as required). See **`tools/tiaportal-mcp/skill/SKILL.md`** §2 and the “调用方式怎么选” table.

---

## 5. First checks inside TIA session

Ask your assistant to run:

1. `RunCapabilitySelfTest` — environment smoke test  
2. With a project open: `Connect` → `AttachToOpenProject` or create via `CreateProject` → `GetProjectTree`

---

## 6. What Openness cannot do

CPU RUN/STOP read/write, diagnostic buffer, clear selective forces as discrete runtime ops — see **`openness-limitations.md`**. Prefer **OPC UA** for runtime data.

---

## 7. Troubleshooting

| Symptom | Fix |
|---------|-----|
| `TIA Portal not running` | Start TIA first |
| Empty tree | Open a project or use `CreateProject` / `AttachToOpenProject` |
| Openness denied | Windows group + TIA authorization dialog |
| MCP HTTP hangs | Do not use naked POST; use stdio client or full MCP client |

More: **`error-model.md`**. NL recipes: **`TIA_NL_INTENT_RECIPES.md`**.
