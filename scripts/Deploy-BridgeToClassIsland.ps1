# 将 Duty-Agent 桥接插件部署到本机 ClassIsland 安装。
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Deploy-BridgeToClassIsland.ps1
#   powershell ... -PluginsDir "D:\其他路径\data\Plugins" -NoStart
param(
    [string]$PluginsDir = "D:\ClassIsland_app_windows_x64_full_folder\data\Plugins",
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "Duty-Agent-ClassIsland-Bridge\DutyAgentBridge.csproj"
$target = Join-Path $PluginsDir "duty-agent-bridge"

Write-Host "[1/4] Building $project (Release)..."
dotnet build $project -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$outDir = Join-Path $repoRoot "Duty-Agent-ClassIsland-Bridge\bin\Release\net8.0-windows"

Write-Host "[2/4] Stopping ClassIsland (plugin files are locked while running)..."
$proc = Get-Process -Name "ClassIsland", "ClassIsland.Desktop" -ErrorAction SilentlyContinue
if ($proc) {
    $proc | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 3
    $proc = Get-Process -Name "ClassIsland", "ClassIsland.Desktop" -ErrorAction SilentlyContinue
    if ($proc) { $proc | Stop-Process -Force }
    Start-Sleep -Seconds 2
    Write-Host "      stopped."
} else {
    Write-Host "      not running."
}

Write-Host "[3/4] Deploying to $target ..."
New-Item -ItemType Directory -Force -Path $target | Out-Null
# 整个构建输出目录一起拷：插件除主 dll 外还带托管依赖（Microsoft.Win32.SystemEvents.dll）
# 与 runtimes\ 原生子目录，逐文件白名单曾在增删依赖时漏拷（部署后 SystemEvents
# 订阅静默失效）。这里与 release 打包保持同一口径——构建输出即插件内容。
Copy-Item (Join-Path $outDir "*") $target -Recurse -Force

Write-Host "[4/4] Done."
if (-not $NoStart) {
    $desktop = Get-ChildItem (Split-Path -Parent $PluginsDir) -Recurse -Filter "ClassIsland.Desktop.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($desktop) {
        Write-Host "      Starting ClassIsland..."
        Start-Process -FilePath $desktop.FullName
    } else {
        Write-Host "      ClassIsland.Desktop.exe not found; start it manually."
    }
}
