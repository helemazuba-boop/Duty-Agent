@echo off
:: ============================================================
:: Duty-Agent 桌面应用打包脚本
:: 使用方法：双击 build_desktop.bat，或在命令行运行
:: ============================================================
chcp 65001 >nul

echo.
echo ============================================================
echo  Duty-Agent 桌面应用打包
echo ============================================================
echo.

:: 1. 检查 Python
python --version >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 Python，请确保已安装 Python 3.10+ 并加入 PATH
    pause
    exit /b 1
)

:: 2. 安装运行时依赖
echo [Step 1/4] 安装运行时依赖...
pip install pywebview --quiet
if errorlevel 1 (
    echo [错误] pywebview 安装失败，请手动运行：pip install pywebview
    pause
    exit /b 1
)
echo   OK

:: 3. 构建前端（如果 dist 不存在或为空）
echo [Step 2/4] 检查前端构建产物...
set UI_DIR=%~dp0Duty-Agent-UI
if not exist "%UI_DIR%\dist" (
    echo   未找到 dist 目录，跳过前端构建
    echo   如需重新构建，请先运行：
    echo   cd %UI_DIR%  ^&^& npm install ^&^& npm run build
) else (
    echo   dist 目录已存在，跳过前端构建
)
echo   OK

:: 4. 安装 PyInstaller
echo [Step 3/4] 安装 PyInstaller...
pip install pyinstaller --quiet
if errorlevel 1 (
    echo [错误] PyInstaller 安装失败，请手动运行：pip install pyinstaller
    pause
    exit /b 1
)
echo   OK

:: 5. 运行 PyInstaller
echo [Step 4/4] 开始打包（这可能需要几分钟）...
echo.
cd /d "%~dp0Assets_Duty"
pyinstaller desktop\desktop.spec --clean --noconfirm
if errorlevel 1 (
    echo.
    echo [错误] PyInstaller 执行失败
    pause
    exit /b 1
)

echo.
echo ============================================================
echo  打包完成！
echo.
echo  输出目录：Assets_Duty\dist\DutyAgent\DutyAgent.exe
echo  直接运行 DutyAgent.exe 即可启动桌面应用
echo ============================================================
pause
