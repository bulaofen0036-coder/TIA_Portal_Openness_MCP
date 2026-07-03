@echo off
chcp 65001 >nul
rem 一键把本 MCP 注册进 Claude Desktop / Claude Code / Cursor / VS Code（V20）。V21 用户请用 配置MCP.bat。
rem 自动写入正确的 exe 路径并合并到现有配置（保留你已有的其它 MCP server，原配置自动备份为 *.bak）。
if not exist "%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin-v20\Release\net48\TiaMcpServer.exe" (
    echo [错误] 找不到引擎 exe。请确认本脚本和 tools 文件夹在同一目录（整包解压，不要单拷 bat）。
    pause
    exit /b 1
)
echo 正在把 TIA Portal MCP 注册进检测到的 AI 客户端（V20）...
echo.
"%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin-v20\Release\net48\TiaMcpServer.exe" config %*
echo.
echo 完成后请重启对应 AI 客户端。
echo 提示：模型较弱或客户端限工具数（如 VS Code）时，改跑：配置MCP-v20.bat --lite  （只暴露约 40 个核心工具）
echo 提示：连不上/报错时，跑：tia-v20.cmd doctor  一键体检（加 --fix 可自动修 Openness 用户组）
echo 提示：其它未自动写入的宿主，跑：配置MCP-v20.bat --print  复制配置片段手动粘贴
pause
