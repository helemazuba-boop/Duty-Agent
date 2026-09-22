[CmdletBinding()]
param(
    [string]$OutputRoot = "",
    [string]$Configuration = "Release",
    # Optional explicit version (e.g. 1.2.3, tag prefix already stripped).
    # When set: stamps both published assemblies (/p:Version=) and rewrites the
    # bridge manifest.yml version in the STAGING copy only. Without it the
    # csproj/manifest defaults apply — which used to mean every release shipped
    # as 0.50.0 forever and ClassIsland's plugin update check never fired.
    [string]$ReleaseVersion = "",
    [string]$SshHost = "aliyun",
    [string]$RemoteRoot = "/www/wwwroot/alist_storage/Duty-Agent",
    [switch]$SkipWebBuild,
    # Upload is now opt-in: a release build should produce a local, shareable
    # artifact by default instead of pushing to a private server.
    [switch]$Upload,
    # Zip is now the default artifact; -NoZip skips it.
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw "ReleaseVersion '$ReleaseVersion' is not a valid x.y.z[.w] version."
    }
}

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-PathInside {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = Get-NormalizedFullPath $Path
    $fullParent = Get-NormalizedFullPath $Parent
    if (-not $fullParent.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $fullParent += [System.IO.Path]::DirectorySeparatorChar
    }

    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description is outside the expected directory: $fullPath"
    }

    return $fullPath
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function New-UniqueDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$BaseName
    )

    $candidate = Join-Path $Parent $BaseName
    $index = 1
    while (Test-Path -LiteralPath $candidate) {
        $candidate = Join-Path $Parent ("{0}-{1}" -f $BaseName, $index)
        $index++
    }

    New-Item -ItemType Directory -Path $candidate | Out-Null
    return (Get-NormalizedFullPath $candidate)
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$SkipTopLevelNames = @()
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($SkipTopLevelNames -contains $item.Name) {
            continue
        }

        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function ConvertTo-RemoteSingleQuoted {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + $Value.Replace("'", "'\''") + "'"
}

function Invoke-TarGz {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$SourceDirectory
    )

    $archiveParent = Split-Path -Path $ArchivePath -Parent
    New-Item -ItemType Directory -Force -Path $archiveParent | Out-Null

    Invoke-Tool `
        -FilePath "tar" `
        -Arguments @(
            "-czf",
            $ArchivePath,
            "-C",
            $SourceDirectory,
            "."
        ) `
        -WorkingDirectory $root
}

function Read-Utf8Text {
    # version.py / manifest.yml carry CJK text as BOM-less UTF-8; Get-Content
    # under Windows PowerShell 5.1 decodes that with the system ANSI codepage,
    # so stamping must round-trip through the .NET UTF-8 APIs instead.
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

function Write-Utf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    # Preserve the file's original BOM state (Set-Content -Encoding UTF8
    # unconditionally adds one under PS 5.1).
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($hasBom)))
}

function Get-AppVersionFromPyText {
    # Extracts the APP_VERSION value from version.py text (contract C3 single
    # source). Returns $null when the assignment is missing.
    param([Parameter(Mandatory = $true)][string]$Text)
    $match = [System.Text.RegularExpressions.Regex]::Match($Text, '(?m)^APP_VERSION[ \t]*=[ \t]*"([^"\r\n]*)"')
    if (-not $match.Success) {
        return $null
    }
    return $match.Groups[1].Value
}

function ConvertTo-NormalizedVersion {
    # Normalizes "9.9.9" / "9.9.9.0" / "9.9" to a comparable 4-part
    # System.Version (MSBuild pads the Win32 FileVersion resource to
    # Major.Minor.Build.Revision, so "9.9.9" reads back as "9.9.9.0").
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    try {
        $parsed = [version]$Value
    }
    catch {
        return $null
    }

    $build = [Math]::Max($parsed.Build, 0)
    $revision = [Math]::Max($parsed.Revision, 0)
    return New-Object System.Version($parsed.Major, $parsed.Minor, $build, $revision)
}

function Test-SameVersionValue {
    # Numeric version equality tolerant to trailing ".0" padding.
    param(
        [string]$Actual,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    $actualNormalized = ConvertTo-NormalizedVersion -Value $Actual
    $expectedNormalized = ConvertTo-NormalizedVersion -Value $Expected
    if ($null -eq $actualNormalized -or $null -eq $expectedNormalized) {
        return $false
    }
    return ($actualNormalized.Equals($expectedNormalized))
}

$root = Get-NormalizedFullPath (Join-Path $PSScriptRoot "..")
$uiDir = Join-Path $root "duty-agent-ui"
$assetsDir = Join-Path $root "Assets_Duty"
$webDir = Join-Path $assetsDir "web"
$clientProject = Join-Path $root "DutyAgent.Client\DutyAgent.Client.csproj"
$bridgeDir = Join-Path $root "Duty-Agent-ClassIsland-Bridge"
$bridgeProject = Join-Path $bridgeDir "DutyAgentBridge.csproj"
$bridgeManifest = Join-Path $bridgeDir "manifest.yml"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root ".release"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $root $OutputRoot
}

$outputRootFull = Get-NormalizedFullPath $OutputRoot

Write-Host ""
Write-Host "============================================================"
Write-Host " Duty-Agent standalone client release"
Write-Host "============================================================"
Write-Host ""

if (-not (Test-Path -LiteralPath $uiDir)) {
    throw "Missing UI directory: $uiDir"
}

if (-not (Test-Path -LiteralPath $clientProject)) {
    throw "Missing client project: $clientProject"
}

if (-not (Test-Path -LiteralPath $bridgeProject)) {
    throw "Missing bridge project: $bridgeProject"
}

if (-not (Test-Path -LiteralPath $bridgeManifest)) {
    throw "Missing bridge manifest: $bridgeManifest"
}

if (-not (Test-Path -LiteralPath (Join-Path $assetsDir "python-embed\python.exe"))) {
    throw "Missing embedded Python: $(Join-Path $assetsDir "python-embed\python.exe")"
}

if (-not (Test-Path -LiteralPath (Join-Path $assetsDir "core.py"))) {
    throw "Missing backend entry: $(Join-Path $assetsDir "core.py")"
}

$versionPy = Join-Path $assetsDir "version.py"
if (-not (Test-Path -LiteralPath $versionPy)) {
    throw "Missing version single source (contract C3): $versionPy"
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    # Contract C3: Assets_Duty\version.py is the version single source the
    # backend imports at runtime, so the release stamps it alongside the two
    # csproj (/p:Version) and the bridge manifest. Only the quoted value of
    # the APP_VERSION line is rewritten; comments, docstring, line endings
    # and the rest of the file stay byte-identical.
    Write-Host "[0/7] Stamping Assets_Duty\version.py -> $ReleaseVersion ..."
    $versionPyText = Read-Utf8Text -Path $versionPy
    $appVersionLinePattern = '(?m)^APP_VERSION[ \t]*=[ \t]*"[^"\r\n]*"'
    $appVersionLines = [System.Text.RegularExpressions.Regex]::Matches($versionPyText, $appVersionLinePattern)
    if ($appVersionLines.Count -eq 0) {
        throw "version.py has no APP_VERSION assignment line to stamp."
    }
    if ($appVersionLines.Count -gt 1) {
        throw "version.py has $($appVersionLines.Count) APP_VERSION lines; expected exactly one."
    }
    $versionPyText = [System.Text.RegularExpressions.Regex]::Replace(
        $versionPyText,
        $appVersionLinePattern,
        ('APP_VERSION = "{0}"' -f $ReleaseVersion))
    Write-Utf8Text -Path $versionPy -Text $versionPyText
    $stampedVersion = Get-AppVersionFromPyText -Text (Read-Utf8Text -Path $versionPy)
    if ($stampedVersion -ne $ReleaseVersion) {
        throw "version.py stamp verification failed: expected '$ReleaseVersion', file now reports '$stampedVersion'."
    }
}

# A python-embed tree that packages cleanly but cannot import its wheels would
# ship a client that dies on backend startup ("packages fine, never runs").
# fastapi/uvicorn/pydantic/httpx are hard imports of core.py; cryptography and
# mcp are the binary wheels shipped inside python-embed.
Write-Host "[0/7] Smoke-testing the embedded Python runtime..."
$embedPython = Join-Path $assetsDir "python-embed\python.exe"
Invoke-Tool `
    -FilePath $embedPython `
    -Arguments @("-c", "import fastapi, uvicorn, pydantic, httpx, cryptography, mcp") `
    -WorkingDirectory $root

if (-not $SkipWebBuild) {
    $uiIcon = Join-Path $uiDir "src\assets\icon.png"
    if (-not (Test-Path -LiteralPath $uiIcon)) {
        Write-Host "[pre] Copying icon.png to UI assets..."
        Copy-Item -LiteralPath (Join-Path $root "icon.png") -Destination $uiIcon -Force
    }

    if (-not (Test-Path -LiteralPath (Join-Path $uiDir "node_modules"))) {
        Write-Host "[pre] UI dependencies not found, running npm install..."
        Invoke-Tool -FilePath "npm.cmd" -Arguments @("install") -WorkingDirectory $uiDir
    }

    Write-Host "[1/7] Building web UI..."
    Invoke-Tool -FilePath "npm.cmd" -Arguments @("run", "build") -WorkingDirectory $uiDir
}
else {
    Write-Host "[1/7] Skipping web UI build."
}

$distDir = Join-Path $uiDir "dist"
if (-not (Test-Path -LiteralPath (Join-Path $distDir "index.html"))) {
    throw "Missing UI build output: $(Join-Path $distDir "index.html")"
}

if ($SkipWebBuild) {
    # -SkipWebBuild ships the existing dist as-is. If any UI source file is
    # newer than dist\index.html the package would silently carry stale UI,
    # so fail and ask for a real build instead.
    $newestUiSource = Get-ChildItem -LiteralPath (Join-Path $uiDir "src") -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    $distIndexHtml = Get-Item -LiteralPath (Join-Path $distDir "index.html")
    if ($null -ne $newestUiSource -and $newestUiSource.LastWriteTime -gt $distIndexHtml.LastWriteTime) {
        throw "Stale web artifact: '$($distIndexHtml.FullName)' ($($distIndexHtml.LastWriteTime)) is older than UI source " +
            "'$($newestUiSource.FullName)' ($($newestUiSource.LastWriteTime)). " +
            "Run 'npm run build' in duty-agent-ui first, or drop -SkipWebBuild."
    }
}

# A dev token baked into the build overrides the per-boot token the host passes
# via the URL, so every /api/v1 call 401s for the lifetime of the artifact.
$builtIndexHtml = Get-Content -LiteralPath (Join-Path $distDir "index.html") -Raw
if ($builtIndexHtml -match '__DEV_TOKEN__') {
    throw "Built UI index.html contains __DEV_TOKEN__ (dev token leaked into the release build). " +
        "Remove VITE_BACKEND_TOKEN from duty-agent-ui\.env.local and rebuild."
}

Write-Host "[2/7] Syncing web UI to Assets_Duty\web..."
$safeWebDir = Assert-PathInside -Path $webDir -Parent $root -Description "Web output directory"
if (Test-Path -LiteralPath $safeWebDir) {
    Remove-Item -LiteralPath $safeWebDir -Recurse -Force
}
Copy-DirectoryContents -Source $distDir -Destination $safeWebDir

if (-not (Test-Path -LiteralPath (Join-Path $webDir "index.html"))) {
    throw "Failed to create Assets_Duty\web\index.html"
}

Write-Host "[3/7] Creating release directory..."
New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$releaseDir = New-UniqueDirectory -Parent $outputRootFull -BaseName "duty-agent-$stamp"
$clientReleaseDir = Join-Path $releaseDir "client"
$bridgeReleaseDir = Join-Path $releaseDir "bridge"
New-Item -ItemType Directory -Force -Path $clientReleaseDir | Out-Null
New-Item -ItemType Directory -Force -Path $bridgeReleaseDir | Out-Null
$msbuildDir = Join-Path $outputRootFull ".msbuild"
$clientMsbuildBinDir = Join-Path $msbuildDir "client-bin\"
$bridgeMsbuildBinDir = Join-Path $msbuildDir "bridge-bin\"

Write-Host "[4/7] Publishing Windows client..."
$clientPublishArgs = @(
    "publish",
    $clientProject,
    "-c",
    $Configuration,
    "-o",
    $clientReleaseDir,
    "--self-contained",
    "-r",
    "win-x64",
    "/p:AssemblyName=duty-agent",
    "/p:BaseOutputPath=$clientMsbuildBinDir"
)
if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $clientPublishArgs += "/p:Version=$ReleaseVersion"
}
Invoke-Tool -FilePath "dotnet" -Arguments $clientPublishArgs -WorkingDirectory $root

$mainExe = Join-Path $clientReleaseDir "duty-agent.exe"
$legacyExe = Join-Path $clientReleaseDir "DutyAgent.Client.exe"
if (-not (Test-Path -LiteralPath $mainExe) -and (Test-Path -LiteralPath $legacyExe)) {
    Move-Item -LiteralPath $legacyExe -Destination $mainExe -Force
}

if (-not (Test-Path -LiteralPath $mainExe)) {
    throw "Publish completed, but duty-agent.exe was not found in: $clientReleaseDir"
}

Write-Host "[5/7] Copying backend assets..."
$releaseAssetsDir = Join-Path $clientReleaseDir "Assets_Duty"
Copy-DirectoryContents -Source $assetsDir -Destination $releaseAssetsDir -SkipTopLevelNames @("data", "__pycache__")

foreach ($cacheDir in Get-ChildItem -LiteralPath $releaseAssetsDir -Directory -Recurse -Filter "__pycache__" -ErrorAction SilentlyContinue) {
    Remove-Item -LiteralPath $cacheDir.FullName -Recurse -Force
}

foreach ($compiledFile in Get-ChildItem -LiteralPath $releaseAssetsDir -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @(".pyc", ".pyo") }) {
    Remove-Item -LiteralPath $compiledFile.FullName -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $releaseAssetsDir "python-embed\python.exe"))) {
    throw "Release is missing Assets_Duty\python-embed\python.exe"
}

if (-not (Test-Path -LiteralPath (Join-Path $releaseAssetsDir "core.py"))) {
    throw "Release is missing Assets_Duty\core.py"
}

if (-not (Test-Path -LiteralPath (Join-Path $releaseAssetsDir "web\index.html"))) {
    throw "Release is missing Assets_Duty\web\index.html"
}

Write-Host "[6/7] Publishing ClassIsland bridge..."
$bridgePublishArgs = @(
    "publish",
    $bridgeProject,
    "-c",
    $Configuration,
    "-o",
    $bridgeReleaseDir,
    "--no-self-contained",
    "/p:BaseOutputPath=$bridgeMsbuildBinDir"
)
if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $bridgePublishArgs += "/p:Version=$ReleaseVersion"
}
Invoke-Tool -FilePath "dotnet" -Arguments $bridgePublishArgs -WorkingDirectory $root

Copy-Item -LiteralPath $bridgeManifest -Destination (Join-Path $bridgeReleaseDir "manifest.yml") -Force

if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    # Stamp the STAGING manifest only (source stays untouched): ClassIsland's
    # plugin update check keys off manifest version, so shipping every release
    # as the hardcoded 0.50.0 silently disabled upgrade detection.
    $stagedManifest = Join-Path $bridgeReleaseDir "manifest.yml"
    # UTF-8 in/out: manifest.yml carries CJK strings and is BOM-less, which
    # Get-Content/Set-Content under PS 5.1 would round-trip as ANSI mojibake.
    $manifestText = Read-Utf8Text -Path $stagedManifest
    $manifestVersionPattern = '(?m)^version:[ \t]*[^\r\n]*'
    $manifestVersionLines = [System.Text.RegularExpressions.Regex]::Matches($manifestText, $manifestVersionPattern)
    if ($manifestVersionLines.Count -ne 1) {
        throw "manifest.yml has no (or more than one) 'version:' line to stamp."
    }
    $manifestText = [System.Text.RegularExpressions.Regex]::Replace(
        $manifestText,
        $manifestVersionPattern,
        ("version: {0}" -f $ReleaseVersion))
    Write-Utf8Text -Path $stagedManifest -Text $manifestText
}

if (-not (Test-Path -LiteralPath (Join-Path $bridgeReleaseDir "DutyAgentBridge.dll"))) {
    throw "Bridge release is missing DutyAgentBridge.dll"
}

if (-not (Test-Path -LiteralPath (Join-Path $bridgeReleaseDir "manifest.yml"))) {
    throw "Bridge release is missing manifest.yml"
}

# Contract C3: after stamping, all four version surfaces must agree before
# anything is bundled or zipped: version.py (the runtime single source), the
# two published binaries (duty-agent.exe / DutyAgentBridge.dll carry the
# /p:Version), and the staged bridge manifest (ClassIsland's update check
# reads it). Otherwise one release reports three different versions.
$appVersionSurfaces = [ordered]@{
    "Assets_Duty\version.py APP_VERSION"  = (Get-AppVersionFromPyText -Text (Read-Utf8Text -Path $versionPy))
    "duty-agent.exe FileVersion"          = (Get-Item -LiteralPath $mainExe).VersionInfo.FileVersion
    "DutyAgentBridge.dll FileVersion"     = (Get-Item -LiteralPath (Join-Path $bridgeReleaseDir "DutyAgentBridge.dll")).VersionInfo.FileVersion
    "bridge manifest.yml version"         = $null
}
$stagedManifestVersionMatch = [System.Text.RegularExpressions.Regex]::Match(
    (Read-Utf8Text -Path (Join-Path $bridgeReleaseDir "manifest.yml")),
    '(?m)^version:[ \t]*([^ \t\r\n]+)')
if ($stagedManifestVersionMatch.Success) {
    $appVersionSurfaces["bridge manifest.yml version"] = $stagedManifestVersionMatch.Groups[1].Value
}
$surfaceSummary = ($appVersionSurfaces.GetEnumerator() | ForEach-Object { "  {0}: {1}" -f $_.Key, $_.Value }) -join "`r`n"

if (-not [string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    foreach ($surface in $appVersionSurfaces.GetEnumerator()) {
        if (-not (Test-SameVersionValue -Actual $surface.Value -Expected $ReleaseVersion)) {
            throw "Version stamp mismatch (expected $ReleaseVersion everywhere):`r`n$surfaceSummary"
        }
    }
    Write-Host "[6/7] Version consistency OK: all four surfaces report $ReleaseVersion."
}
else {
    # No explicit stamp: defaults apply. The bridge dll and the manifest then
    # carry their checked-in versions, which must still agree with version.py.
    # duty-agent.exe is exempt: DutyAgent.Client.csproj declares no <Version>,
    # so MSBuild's 1.0.0 default is expected there until it gets one.
    $defaultVersion = $appVersionSurfaces["Assets_Duty\version.py APP_VERSION"]
    if ([string]::IsNullOrWhiteSpace($defaultVersion)) {
        throw "Assets_Duty\version.py carries no APP_VERSION value (contract C3 broken)."
    }
    foreach ($surfaceName in @("DutyAgentBridge.dll FileVersion", "bridge manifest.yml version")) {
        if (-not (Test-SameVersionValue -Actual $appVersionSurfaces[$surfaceName] -Expected $defaultVersion)) {
            throw "Default versions drift from version.py (expected $defaultVersion everywhere it is declared):`r`n$surfaceSummary"
        }
    }
    Write-Host "[6/7] Default version consistency OK: $defaultVersion (duty-agent.exe not stamped; MSBuild default applies)."
}

# Bundle the bridge inside the client package so the client's one-click
# "Install Bridge to ClassIsland" button has a local source to copy from.
Write-Host "[6/7] Bundling bridge into client package..."
$clientBridgeDir = Join-Path $clientReleaseDir "bridge"
Copy-DirectoryContents -Source $bridgeReleaseDir -Destination $clientBridgeDir

Write-Host "[6/7] Writing README.txt..."
# The user-facing README contains Chinese; keep it in a separate shipped file
# (Assets_Duty\README-client.txt) rather than inline here, so this script stays
# ASCII-only and parses correctly under Windows PowerShell 5.1 (which reads a
# UTF-8 no-BOM .ps1 as the system codepage and would mangle inline CJK).
$readmeSource = Join-Path $assetsDir "README-client.txt"
$readmeTarget = Join-Path $releaseDir "README.txt"
if (Test-Path -LiteralPath $readmeSource) {
    Copy-Item -LiteralPath $readmeSource -Destination $readmeTarget -Force
}
else {
    Set-Content -Path $readmeTarget -Value "See Assets_Duty/README-client.txt" -Encoding UTF8
}

$zipPath = $null
if (-not $NoZip) {
    Write-Host "[extra] Creating zip archive..."
    $zipPath = "$releaseDir.zip"
    Compress-Archive -Path (Join-Path $releaseDir "*") -DestinationPath $zipPath -Force

    # Minimal backend package: Assets_Duty + duty-cli.bat, for AI-harness /
    # headless scenarios that do not need the Windows client.
    Write-Host "[extra] Creating minimal backend package..."
    $backendStage = New-UniqueDirectory -Parent $msbuildDir -BaseName "backend-min"
    Copy-DirectoryContents -Source $releaseAssetsDir -Destination (Join-Path $backendStage "Assets_Duty")
    Copy-Item -LiteralPath (Join-Path $root "duty-cli.bat") -Destination $backendStage -Force
    $backendZipPath = "$releaseDir-backend.zip"
    Compress-Archive -Path (Join-Path $backendStage "*") -DestinationPath $backendZipPath -Force
}

$remoteReleaseDir = $null
if ($Upload) {
    Write-Host "[7/7] Uploading release to SSH host '$SshHost'..."
    if (-not (Get-Command "ssh" -ErrorAction SilentlyContinue)) {
        throw "ssh was not found in PATH."
    }

    if (-not (Get-Command "scp" -ErrorAction SilentlyContinue)) {
        throw "scp was not found in PATH."
    }

    $remoteRootTrimmed = $RemoteRoot.TrimEnd("/")
    $releaseName = Split-Path -Path $releaseDir -Leaf
    $remoteReleaseDir = "$remoteRootTrimmed/$releaseName"
    $remoteArchivePath = "$remoteRootTrimmed/$releaseName.tar.gz"
    $quotedRemoteReleaseDir = ConvertTo-RemoteSingleQuoted $remoteReleaseDir
    $quotedRemoteRoot = ConvertTo-RemoteSingleQuoted $remoteRootTrimmed
    $quotedRemoteArchivePath = ConvertTo-RemoteSingleQuoted $remoteArchivePath

    $tarGzPath = "$releaseDir.tar.gz"
    Write-Host "[7/7] Creating upload archive..."
    Invoke-TarGz -ArchivePath $tarGzPath -SourceDirectory $releaseDir

    Invoke-Tool `
        -FilePath "ssh" `
        -Arguments @("-o", "BatchMode=yes", "-o", "ConnectTimeout=15", $SshHost, "mkdir -p $quotedRemoteRoot") `
        -WorkingDirectory $root

    Invoke-Tool `
        -FilePath "scp" `
        -Arguments @("-o", "BatchMode=yes", "-o", "ConnectTimeout=15", $tarGzPath, "${SshHost}:$remoteArchivePath") `
        -WorkingDirectory $root

    Invoke-Tool `
        -FilePath "ssh" `
        -Arguments @(
            "-o",
            "BatchMode=yes",
            "-o",
            "ConnectTimeout=15",
            $SshHost,
            "rm -rf $quotedRemoteReleaseDir && mkdir -p $quotedRemoteReleaseDir && tar -xzf $quotedRemoteArchivePath -C $quotedRemoteReleaseDir"
        ) `
        -WorkingDirectory $root
}
else {
    Write-Host "[7/7] Skipping SSH upload."
}

Write-Host ""
Write-Host "============================================================"
Write-Host " Done"
Write-Host " Release: $releaseDir"
Write-Host " Client: $clientReleaseDir"
Write-Host " Main exe: $mainExe"
Write-Host " Bridge: $bridgeReleaseDir"
if ($remoteReleaseDir) {
    Write-Host " Remote: ${SshHost}:$remoteReleaseDir"
}
if ($zipPath) {
    Write-Host " Archive: $zipPath"
}
Write-Host "============================================================"
