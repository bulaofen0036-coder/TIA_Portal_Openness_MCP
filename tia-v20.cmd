@echo off
chcp 65001 >nul
rem `tia` 命令入口（V20）。把本交付包根目录加入 PATH 后，可直接：tia-v20 gen spec.yaml
rem V21 用户请用同目录的 tia.cmd。所有参数原样透传给引擎 exe。
rem 交付 zip 布局在 tools\...\bin-v20\Release\net48；git 克隆布局在 runtime\v20（若发布包含）。
set "EXE=%~dp0runtime\v20\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin-v20\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" (
  echo [错误] 找不到 V20 引擎 exe。git 克隆只带 V21 运行时；V20 请到 GitHub Releases 下载交付 zip。
  exit /b 2
)
"%EXE%" %*
exit /b %ERRORLEVEL%
