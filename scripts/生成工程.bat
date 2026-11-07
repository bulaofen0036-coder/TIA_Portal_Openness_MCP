@echo off
chcp 65001 >nul
setlocal
rem 把一个 spec.yaml / spec.json 拖到本文件图标上即可生成博途工程。
rem 默认用 V21 exe；若不存在则回退到 V20 exe（按你装的 TIA 大版本自动选）。
set "EXE=%~dp0..\runtime\v21\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0..\runtime\v20\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0..\tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0..\tools\tiaportal-mcp\src\TiaMcpServer\bin-v20\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" (
  echo 找不到 tia 可执行文件（tools\...\bin、bin-v20 与 runtime\ 均不存在）。
  echo 请确认本 .bat 位于交付包/仓库的 scripts\ 目录内（整包解压或完整克隆）。
  pause
  exit /b 2
)
if "%~1"=="" (
  echo 用法: 把一个 spec.yaml 或 spec.json 拖到本文件上,
  echo       或运行:  生成工程.bat ^<spec 文件^>
  pause
  exit /b 2
)
"%EXE%" gen "%~1"
echo.
echo 退出码 %ERRORLEVEL%   ^(0=成功  1=有失败步骤  2=错误^)
pause
