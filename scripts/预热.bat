@echo off
chcp 65001 >nul
setlocal
rem 预热一个常驻 headless TIA 实例：跑一次留着，之后每次 tia 命令约 1 秒连上。
rem 按 Ctrl+C 停止并关闭该实例。
set "EXE=%~dp0..\runtime\v21\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0..\runtime\v20\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0..\tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0..\tools\tiaportal-mcp\src\TiaMcpServer\bin-v20\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" (
  echo 找不到 tia 可执行文件（tools\...\bin、bin-v20 与 runtime\ 均不存在）。
  pause
  exit /b 2
)
echo 正在冷启动 headless TIA 并保活，按 Ctrl+C 停止...
"%EXE%" prewarm
