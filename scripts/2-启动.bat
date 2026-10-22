@echo off
setlocal
title Duty-Agent - Start
set "ROOT=%~dp0"

rem ---------------------------------------------------------------------------
rem  Double-click entry: launch the installed MSIX package by AUMID.
rem  No elevation on purpose - starting the app elevated would give the client
rem  an admin token it never needs (and would put its HKCU writes in the wrong
rem  hive if the tester is a different user).
rem
rem  Package name is read from build-info.json so this file never hardcodes an
rem  identity that changes with Publisher/Name.
rem ---------------------------------------------------------------------------

set "PKGNAME="
for /f "usebackq delims=" %%i in (`powershell.exe -NoProfile -Command "try { $j = Get-Content -LiteralPath '%ROOT%build-info.json' -Raw -Encoding UTF8; $o = ConvertFrom-Json -InputObject $j; if ($o.name) { $o.name } } catch { }"`) do set "PKGNAME=%%i"

if not defined PKGNAME (
    echo [ERROR] Cannot read build-info.json next to this script.
    echo         Run 1-install.bat from the test bundle folder instead.
    echo.
    pause
    exit /b 1
)

set "PFN="
for /f "usebackq delims=" %%i in (`powershell.exe -NoProfile -Command "$p = Get-AppxPackage -Name '%PKGNAME%' -ErrorAction SilentlyContinue; if ($p) { $p.PackageFamilyName }"`) do set "PFN=%%i"

if not defined PFN (
    echo [ERROR] Package "%PKGNAME%" is not installed on this machine.
    echo         Run 1-Install.bat first.
    echo.
    pause
    exit /b 1
)

echo Starting %PFN% ...
start "" explorer.exe "shell:AppsFolder\%PFN%!App"

echo.
echo Launched via: shell:AppsFolder\%PFN%!App
echo If nothing appears, start it from the Start menu instead (search Duty-Agent).
timeout /t 6 >nul
exit /b 0
