@echo off
chcp 65001 >nul
rem 一键把本 MCP 注册进 Claude Desktop / Claude Code / Cursor / VS Code（V20）。V21 用户请用 配置MCP.bat。
rem 自动写入正确的 exe 路径并合并到现有配置（保留你已有的其它 MCP server，原配置自动备份为 *.bak）。
rem V20 引擎 exe 位置：交付 zip 在 tools\...\bin-v20\Release\net48；git 克隆在 runtime\v20（若发布包含）。
set "EXE=%~dp0runtime\v20\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin-v20\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" (
    echo [错误] 找不到 V20 引擎 exe。git 克隆只带 V21 运行时（runtime\v21）；
    echo V20 请到 GitHub Releases 下载交付 zip（含 V20 exe），整包解压后再运行本脚本。
    pause
    exit /b 1
)
echo 正在把 TIA Portal MCP 注册进检测到的 AI 客户端（V20）...
echo.
"%EXE%" config %*
echo.
echo 完成后请重启对应 AI 客户端。
echo 提示：默认写入精简档（62 个核心工具，弱模型/VS Code 也稳）。要全量 227 个工具改跑：配置MCP-v20.bat --full
echo 提示：连不上/报错时，跑：tia-v20.cmd doctor  一键体检（加 --fix 可自动修 Openness 用户组）
echo 提示：其它未自动写入的宿主，跑：配置MCP-v20.bat --print  复制配置片段手动粘贴
pause
