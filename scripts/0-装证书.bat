@echo off
setlocal
title Duty-Agent MSIX - Trust Dev Certificate
set "ROOT=%~dp0"
set "CLI_ARGS=%*"

rem ---------------------------------------------------------------------------
rem  Double-click entry for the MSIX test bundle: trust the dev certificate in
rem  LocalMachine\TrustedPeople. This is the ONLY step that needs administrator
rem  rights; after this the .msix installs itself (double-click / App Installer)
rem  with no elevation at all.
rem
rem  ASCII-only on purpose: cmd renders .bat files in the OEM code page, so any
rem  Chinese in here would be mojibake. All localized text comes from the .ps1,
rem  which carries a UTF-8 BOM and prints correctly.
rem
rem  NOTE: the admin check and the argument guard must live at TOP LEVEL.
rem  Inside a "call :Label" subroutine, %1 / %* are the subroutine's own
rem  arguments (empty here), so a guard written there never fires.
rem
rem  NOTE: do NOT pass the directory as -Root. %~dp0 ends with a backslash, so
rem  -Root "%ROOT%" reaches PowerShell as -Root "D:\...\" - the \" is parsed as
rem  an escaped quote and the argument silently gains a trailing quote, which
rem  then makes every Test-Path fail with "illegal characters in path". The
rem  .ps1 resolves the bundle from its own $PSScriptRoot instead, so there is
rem  nothing to pass.
rem ---------------------------------------------------------------------------

powershell.exe -NoProfile -Command "if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 1 }" >nul 2>&1
rem "errorlevel 1" is true when errorlevel >= 1, i.e. the probe failed, i.e.
rem we are NOT an administrator yet - that is the only case that needs elevating.
if errorlevel 1 goto :RunAsAdmin

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Import-DevCertificate.ps1"
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo [OK] Dev certificate is trusted. You can now double-click the .msix
    echo      ^(no administrator rights needed for the install itself^).
) else (
    echo [FAILED] Returned %RC%. Scroll up for the reason.
)
echo.
pause
exit /b %RC%

:RunAsAdmin
rem  Elevation spawns a NEW process, so command-line arguments do not survive
rem the hop. Refuse instead of silently swallowing them.
if defined CLI_ARGS (
    echo [INFO] Called with arguments, but this launcher elevates by spawning a
    echo        copy of itself - arguments do not survive that hop.
    echo.
    echo        Right-click this .bat and choose "Run as administrator",
    echo        or run Import-DevCertificate.ps1 from an elevated shell instead.
    echo.
    pause
    exit /b 1
)

echo Requesting administrator privileges...
powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" >nul 2>&1
exit /b 1
