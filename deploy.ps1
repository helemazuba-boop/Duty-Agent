$ErrorActionPreference = "Stop"

# 1. Compile the project
Write-Host "1. Compiling project..." -ForegroundColor Cyan
dotnet build -c Debug
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

# 2. Kill ClassIsland process
Write-Host "2. Killing ClassIsland process..." -ForegroundColor Cyan
$processes = Get-Process | Where-Object { $_.ProcessName -eq "ClassIsland" -or $_.ProcessName -eq "ClassIsland.Desktop" }

if ($processes) {
    foreach ($proc in $processes) {
        Write-Host "Stopping $($proc.ProcessName) (ID: $($proc.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2 # Wait for release
}
else {
    Write-Host "ClassIsland is not running." -ForegroundColor Yellow
}

# 3. Replace plugin files
$sourceDir = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows"
$destDir = "D:\ClassIsland_app_windows_x64_full_folder\data\Plugins\Duty-Agent"

Write-Host "3. Replacing plugin files..." -ForegroundColor Cyan
Write-Host "Source: $sourceDir"
Write-Host "Destination: $destDir"

if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

# Copy contents with retry
$maxRetries = 5
$retryCount = 0
$copied = $false

while (-not $copied -and $retryCount -lt $maxRetries) {
    try {
        Copy-Item -Path "$sourceDir\*" -Destination $destDir -Recurse -Force -ErrorAction Stop
        $copied = $true
        Write-Host "Files deployed." -ForegroundColor Green
    }
    catch {
        Write-Warning "File copy failed. Retrying in 1 second... ($($_.Exception.Message))"
        Start-Sleep -Seconds 1
        $retryCount++
    }
}

if (-not $copied) {
    Write-Error "Failed to copy files after $maxRetries retries."
    exit 1
}

# 4. Start ClassIsland
$exePath = "D:\ClassIsland_app_windows_x64_full_folder\ClassIsland.exe"
Write-Host "4. Starting ClassIsland..." -ForegroundColor Cyan
if (Test-Path $exePath) {
    Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath -Parent)
    Write-Host "ClassIsland started." -ForegroundColor Green
}
else {
    Write-Error "ClassIsland executable not found at $exePath"
}
