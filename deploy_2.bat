@echo off
setlocal enabledelayedexpansion

:: 设置变量
set "SOURCE_DIR=%~dp0bin\Debug\net8.0-windows"
set "DEST_DIR=D:\ClassIsland_app_windows_x64_full_folder (2)\data\Plugins\Duty-Agent"
set "EXE_PATH=D:\ClassIsland_app_windows_x64_full_folder (2)\ClassIsland.exe"
set "EXE_DIR=D:\ClassIsland_app_windows_x64_full_folder (2)"

:: 0. 同步代码
echo [0/5] 0. Syncing code from GitHub developer branch...
git pull origin developer
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Git sync failed!
    exit /b 1
)

:: 1. 编译项目
echo [1/5] 1. Compiling project...
dotnet build -c Debug
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Build failed!
    exit /b 1
)

:: 2. 结束进程
echo [2/5] 2. Killing ClassIsland process...
taskkill /f /im ClassIsland.exe >nul 2>&1
taskkill /f /im ClassIsland.Desktop.exe >nul 2>&1
:: 稍作延迟确保文件句柄释放
timeout /t 2 /nobreak >nul

:: 3. 替换插件文件
echo [3/5] 3. Replacing plugin files...

:: 删除原有插件文件夹内容，确保部署的是最新版本且无残留
if exist "%DEST_DIR%" (
    echo Cleaning destination folder: %DEST_DIR%
    rd /s /q "%DEST_DIR%"
)
mkdir "%DEST_DIR%"

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
echo [4/5] 4. Starting ClassIsland...
if exist "%EXE_PATH%" (
    :: 使用 start 命令并设置工作目录
    start "" /d "%EXE_DIR%" "%EXE_PATH%"
    echo ClassIsland started.
) else (
    echo [ERROR] ClassIsland executable not found at %EXE_PATH%
    exit /b 1
)

pause
