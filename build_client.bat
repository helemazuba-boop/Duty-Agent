@echo off
setlocal

set "ROOT=%~dp0"

rem Auto version: bump the build segment of <major>.<minor>.<build> when the
rem source changed since the last build (scripts\Version.ps1). The bridge plugin
rem keeps its own version. Pass -NoAutoVersion to use the checked-in one.
set "AUTO=-AutoVersion"
echo %* | findstr /i /c:"-NoAutoVersion" >nul && set "AUTO="

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\New-DutyAgentClientRelease.ps1" %* %AUTO%
exit /b %ERRORLEVEL%
