@echo off
setlocal

set "ROOT=%~dp0"
set "UI_DIR=%ROOT%duty-agent-ui"
set "WEB_DIR=%ROOT%Assets_Duty\web"

echo.
echo ============================================================
echo  Duty-Agent minimal standalone client build
echo ============================================================
echo.

echo [1/3] Building web UI...
pushd "%UI_DIR%" || exit /b 1
call npm.cmd run build
if errorlevel 1 (
  popd
  echo [error] Web UI build failed.
  exit /b 1
)
popd

echo [2/3] Copying web UI dist to Assets_Duty\web...
if exist "%WEB_DIR%" rmdir /s /q "%WEB_DIR%"
mkdir "%WEB_DIR%"
xcopy "%UI_DIR%\dist\*" "%WEB_DIR%\" /e /i /y >nul
if errorlevel 1 (
  echo [error] Failed to copy web UI dist.
  exit /b 1
)

echo [3/3] Building standalone client...
dotnet build "%ROOT%DutyAgent.Client\DutyAgent.Client.csproj" -c Release
if errorlevel 1 (
  echo [error] Client build failed.
  exit /b 1
)

echo.
echo ============================================================
echo  Done
echo  Run: DutyAgent.Client\bin\Release\net8.0-windows\DutyAgent.Client.exe
echo ============================================================
endlocal
