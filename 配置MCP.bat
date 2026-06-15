@echo off
chcp 65001 >nul
rem 一键把本 MCP 注册进 Claude Desktop / Cursor（V21）。V20 用户请改用 配置MCP-v20.bat。
rem 自动写入正确的 exe 路径并合并到现有配置（保留你已有的其它 MCP server，原配置自动备份为 *.bak）。
echo 正在把 TIA Portal MCP 注册进 Claude Desktop / Cursor ...
echo.
"%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe" config %*
echo.
echo 完成后请重启对应 AI 客户端。其它宿主（如 Claude Code）可运行：配置MCP.bat --print 复制配置片段。
pause
