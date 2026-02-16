@echo off
setlocal enabledelayedexpansion

:: 设置变量
set "SOURCE_DIR=%~dp0bin\Debug\net8.0-windows"
set "DEST_DIR=D:\ClassIsland_app_windows_x64_full_folder\data\Plugins\Duty-Agent"
set "EXE_PATH=D:\ClassIsland_app_windows_x64_full_folder\ClassIsland.exe"
set "EXE_DIR=D:\ClassIsland_app_windows_x64_full_folder"

:: 1. 编译项目
echo [1/4] 1. Compiling project...
dotnet build -c Debug
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Build failed!
    exit /b 1
)

:: 2. 结束进程
echo [2/4] 2. Killing ClassIsland process...
taskkill /f /im ClassIsland.exe >nul 2>&1
taskkill /f /im ClassIsland.Desktop.exe >nul 2>&1
:: 稍作延迟确保文件句柄释放
timeout /t 2 /nobreak >nul

:: 3. 替换插件文件
echo [3/4] 3. Replacing plugin files...
if not exist "%DEST_DIR%" mkdir "%DEST_DIR%"

set "MAX_RETRIES=5"
set "RETRY_COUNT=0"
set "COPIED=false"

:COPY_LOOP
xcopy "%SOURCE_DIR%\*" "%DEST_DIR%\" /s /e /y /q
if %ERRORLEVEL% equ 0 (
    set "COPIED=true"
    echo Files deployed.
) else (
    set /a RETRY_COUNT+=1
    if !RETRY_COUNT! lss %MAX_RETRIES% (
        echo [WARNING] File copy failed. Retrying in 1 second... (!RETRY_COUNT!/%MAX_RETRIES%)
        timeout /t 1 /nobreak >nul
        goto COPY_LOOP
    )
)

if "%COPIED%"=="false" (
    echo [ERROR] Failed to copy files after %MAX_RETRIES% retries.
    exit /b 1
)

:: 4. 启动 ClassIsland
echo [4/4] 4. Starting ClassIsland...
if exist "%EXE_PATH%" (
    :: 使用 start 命令并设置工作目录
    start "" /d "%EXE_DIR%" "%EXE_PATH%"
    echo ClassIsland started.
) else (
    echo [ERROR] ClassIsland executable not found at %EXE_PATH%
    exit /b 1
)

pause