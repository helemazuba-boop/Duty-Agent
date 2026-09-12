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
Copy-Item (Join-Path $outDir "DutyAgentBridge.dll") $target -Force
Copy-Item (Join-Path $outDir "DutyAgentBridge.deps.json") $target -Force
Copy-Item (Join-Path $outDir "DutyAgentBridge.runtimeconfig.json") $target -Force
Copy-Item (Join-Path $outDir "icon.png") $target -Force
Copy-Item (Join-Path $repoRoot "Duty-Agent-ClassIsland-Bridge\manifest.yml") $target -Force
Copy-Item (Join-Path $repoRoot "Duty-Agent-ClassIsland-Bridge\README.md") $target -Force

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
