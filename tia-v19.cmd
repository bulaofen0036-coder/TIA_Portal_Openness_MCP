@echo off
chcp 65001 >nul
rem TIA Portal V19 MCP entry point. Works from the repo root and from a release package.
set "EXE=%~dp0TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin-v19\Release\net48\TiaMcpServer.exe"
"%EXE%" %*
exit /b %ERRORLEVEL%
