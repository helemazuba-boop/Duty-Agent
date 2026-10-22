@echo off
setlocal
title Duty-Agent MSIX - Uninstall
set "ROOT=%~dp0"
set "CLI_ARGS=%*"

rem ---------------------------------------------------------------------------
rem  Double-click entry for the MSIX test bundle. Two ways to run it:
rem    1) double-click  - self-elevates below, one UAC prompt;
rem    2) right-click "Run as administrator" - already elevated, falls straight
rem       through to the uninstaller with no second prompt.
rem
rem  The .ps1 also cleans the HKCU Run values written by the packaged client
rem  (DutyAgent.<PFN>), which MSIX has no uninstall hook for.
rem  ASCII-only on purpose; see 1-install.bat for why.
rem  Admin check / argument guard stay at TOP LEVEL - %1 inside a
rem  "call :Label" is the subroutine's own argument, not the script's.
rem ---------------------------------------------------------------------------

powershell.exe -NoProfile -Command "if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 1 }" >nul 2>&1
rem "errorlevel 1" is true when errorlevel >= 1, i.e. the probe failed, i.e.
rem we are NOT an administrator yet - that is the only case that needs elevating.
if errorlevel 1 goto :RunAsAdmin

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Uninstall-MsixForTesting.ps1" %CLI_ARGS%
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo [OK] Uninstall finished.
) else (
    echo [FAILED] Uninstall returned %RC%. Scroll up for the reason.
)
echo.
pause
exit /b %RC%

:RunAsAdmin
rem  Elevation spawns a NEW process, so command-line arguments do not survive
rem  the hop. Refuse instead of silently swallowing them.
if defined CLI_ARGS (
    echo [INFO] Called with arguments, but this launcher elevates by spawning a
    echo        copy of itself - arguments do not survive that hop.
    echo.
    echo        Right-click this .bat and choose "Run as administrator",
    echo        or run Uninstall-MsixForTesting.ps1 from an elevated shell instead.
    echo.
    pause
    exit /b 1
)

echo Requesting administrator privileges...
powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" >nul 2>&1
exit /b 1
