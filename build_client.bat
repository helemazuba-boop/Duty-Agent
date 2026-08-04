@echo off
setlocal

set "ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\New-DutyAgentClientRelease.ps1" %*
exit /b %ERRORLEVEL%
