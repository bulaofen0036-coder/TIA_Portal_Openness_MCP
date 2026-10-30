@echo off
chcp 65001 >nul
rem `tia` 命令入口（V21）。把本交付包根目录加入 PATH 后，可直接：tia gen spec.yaml
rem V20 用户请改用同目录的 tia-v20.cmd。所有参数原样透传给引擎 exe。
rem 交付 zip 与 git 克隆都带 runtime\v21，优先用它；tools\...\bin\Release\net48 只作开发树兜底。
set "EXE=%~dp0runtime\v21\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" (
  echo [错误] 找不到引擎 exe（tools\...\bin\Release\net48 和 runtime\v21 均不存在）。
  exit /b 2
)
"%EXE%" %*
exit /b %ERRORLEVEL%
