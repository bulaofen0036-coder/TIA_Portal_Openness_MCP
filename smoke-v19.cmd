@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
set "EXE=%ROOT%TiaMcpServer.exe"
set "FAIL=0"

echo === TIA Portal V19 MCP smoke test ===
echo Root: %ROOT%

echo.
echo [1/3] version
"%EXE%" version
if errorlevel 1 set "FAIL=1"

echo.
echo [2/3] offline capability self-test
"%EXE%" --run-capability-self-test
if errorlevel 1 set "FAIL=1"

echo.
echo [3/3] gen dry-run
"%EXE%" gen "%ROOT%templates\v19-smoke-spec.json" --dry-run
if errorlevel 1 set "FAIL=1"

if /I "%~1"=="--with-tia" (
  echo.
  echo [4/4] connect self-test, launches TIA
  "%EXE%" --run-capability-self-test --self-test-connect
  if errorlevel 1 set "FAIL=1"
)

echo.
if "%FAIL%"=="0" (
  echo RESULT: PASS offline checks
) else (
  echo RESULT: FAIL
)
echo Real-machine project check is manual: run tia-v19.cmd gen and describe with a real project.
exit /b %FAIL%
