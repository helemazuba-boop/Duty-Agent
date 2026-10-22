@echo off
setlocal

set "ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Build-MsixPackage.ps1" %*
exit /b %ERRORLEVEL%
