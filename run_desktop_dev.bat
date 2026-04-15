@echo off
chcp 65001 >nul
:: ============================================================
:: Duty-Agent 桌面应用（开发模式）
:: 直接启动，无需打包。双击此文件即可。
:: ============================================================
chcp 65001 >nul
echo.
echo [Duty-Agent] 正在检查依赖...
python -c "import webview" 2>nul
if errorlevel 1 (
    echo [错误] pywebview 未安装
    echo 请运行：pip install pywebview
    pause
    exit /b 1
)
echo [OK] pywebview
python -c "import fastapi" 2>nul
if errorlevel 1 (
    echo [错误] fastapi 未安装
    pause
    exit /b 1
)
echo [OK] fastapi
echo.
echo [Duty-Agent] 启动中...
cd /d "%~dp0Assets_Duty"
python desktop\launcher.py
