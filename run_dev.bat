@echo off
:: Duty-Agent Dev Environment Startup
:: Uses Python orchestrator to avoid cmd/batch stdin redirection issues
chcp 65001 >nul
"D:\projects\Duty-Agent\Assets_Duty\python-embed\python.exe" "D:\projects\Duty-Agent\orchestrator.py"
