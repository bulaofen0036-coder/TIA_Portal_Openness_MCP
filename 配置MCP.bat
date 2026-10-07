@echo off
chcp 65001 >nul
rem 一键把本 MCP 注册进 Claude Desktop / Claude Code / Cursor / VS Code（V21）。V20 用户请改用 配置MCP-v20.bat。
rem 自动写入正确的 exe 路径并合并到现有配置（保留你已有的其它 MCP server，原配置自动备份为 *.bak）。
rem 引擎 exe 位置：交付 zip 在 tools\...\bin\Release\net48；git 克隆在 runtime\v21。两处都找。
set "EXE=%~dp0runtime\v21\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" (
    echo [错误] 找不到引擎 exe（tools\...\bin\Release\net48 和 runtime\v21 均不存在）。
    echo 请确认本脚本在交付包/仓库根目录（整包解压或完整克隆，不要单拷 bat）。
    pause
    exit /b 1
)
echo 正在把 TIA Portal MCP 注册进检测到的 AI 客户端（Claude Desktop / Claude Code / Cursor / VS Code）...
echo.
"%EXE%" config %*
echo.
echo 完成后请重启对应 AI 客户端。
echo 提示：默认只列出 62 个核心工具（其余 160 个 AI 用 FindTools/CallTool 随用随取，能力不缺）。
echo 提示：想一次列全部 224 个工具，跑：配置MCP.bat --full （注意 VS Code/Copilot 上限 128、Windsurf 100，超了会直接加载失败）
echo 提示：连不上/报错时，跑：tia.cmd doctor  一键体检（加 --fix 可自动修 Openness 用户组）
echo 提示：其它未自动写入的宿主，跑：配置MCP.bat --print  复制配置片段手动粘贴
pause
