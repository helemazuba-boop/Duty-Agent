@echo off
:: Duty-Agent Dev Environment Startup
:: Uses Python orchestrator to avoid cmd/batch stdin redirection issues
::
:: 用法:
::   run_dev.bat           — 跳过 Token 鉴权（开发调试，默认）
::   run_dev.bat full-auth — 完整鉴权模式
chcp 65001 >nul
setlocal enabledelayedexpansion
set "ARGS=--skip-auth"
if /i "%~1"=="full-auth" set "ARGS="
"%~dp0Assets_Duty\python-embed\python.exe" "%~dp0orchestrator.py" %ARGS%
