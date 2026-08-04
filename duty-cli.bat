@echo off
:: Duty-Agent CLI thin wrapper.
:: Delegates to the bundled embedded Python and the CLI entry point.
:: Usage: duty-cli.bat <command> [options]   (see: duty-cli.bat describe)
chcp 65001 >nul
"%~dp0Assets_Duty\python-embed\python.exe" "%~dp0Assets_Duty\cli.py" %*
