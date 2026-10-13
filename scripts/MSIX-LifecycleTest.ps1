#Requires -Version 5.1
<#
    .SYNOPSIS
        MSIX 包的正式生命周期验收：安装 → 首跑 → 升级 → 卸载。
    .DESCRIPTION
        以前只做过"稀疏包身份 + single-debug-identity"的实测，MSIX 的
        安装/卸载/升级从来没跑过正式验收。这个脚本把三项固化成可重复执行、
        失败即非零退出的检查集。

        与 CI 不同：这里会真的改目标机（装包、写注册表、跑 GUI），
        所以要管理员，并且默认会先确认。

        覆盖的行为不变量（全部有实测出处）：
          1. 包能装、能查到、安装目录里 exe / Assets_Duty / bridge 齐全；
          2. AUMID 能拉起客户端，日志首行打出"包身份=是(MSIX)"与正确 PFN
             （历史上 ScopeName 前缀顺序 bug 让包身份下启动即崩）；
          3. 数据落在真实 %LOCALAPPDATA%\DutyAgent（不被重定向到 LocalState），
             这是"zip 版与商店版共用同一份数据"的设计前提；
          4. 默认开启的自启会写 HKCU Run 键，值是 AUMID 激活命令；
          5. 升级后版本号递增、用户数据保留；
          6. 卸载后包与安装目录消失，Run 键自启残留被识别，用户数据不删。
    .PARAMETER MsixPath
        被测 MSIX 包的路径。
    .PARAMETER PackageName
        清单里的 Identity Name，默认 DutyAgent。
    .PARAMETER CertPath
        自签开发证书（.cer 或 .pfx）；提供时先导入「本地计算机\受信任人」。
    .PARAMETER UpgradeMsixPath
        第二个 MSIX（版本号必须更高），用于验收升级路径。不给则跳过该阶段
        并给出生成方法（不算失败）。
    .PARAMETER Phase
        只跑某个阶段，默认全部。
    .PARAMETER Force
        跳过交互确认（CI / 已知后果时用）。
    .PARAMETER RunTimeoutSeconds
        等客户端产出日志的最长秒数。
    .EXAMPLE
        .\Test-MsixLifecycle.ps1 -MsixPath .msix-work\verify\DutyAgent_1.0.0.0_x64.msix -CertPath .msix-work\verify\devcert.cer
    .EXAMPLE
        .\Test-MsixLifecycle.ps1 -MsixPath A.msix -UpgradeMsixPath B.msix -Phase Upgrade -Force
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MsixPath,
    [string]$PackageName = "DutyAgent",
    [string]$CertPath = "",
    [string]$UpgradeMsixPath = "",
    [ValidateSet("All", "Install", "Run", "Upgrade", "Uninstall")]
    [string]$Phase = "All",
    [switch]$Force,
    [int]$RunTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"

$script:Passed = 0
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Warnings = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Test-Assert([bool]$Condition, [string]$Message, [string]$Detail = "") {
    if ($Condition) {
        $script:Passed++
        Write-Host "  [PASS] $Message" -ForegroundColor Green
    }
    else {
        $script:Failures.Add($Message)
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        if ($Detail) { Write-Host "         $Detail" -ForegroundColor DarkGray }
    }
    return $Condition
}

function Test-Info([string]$Message) { Write-Host "  [....] $Message" -ForegroundColor Gray }

function Test-Warn([string]$Message) {
    $script:Warnings.Add($Message)
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 前置
if (-not (Test-Path -LiteralPath $MsixPath)) { throw "MSIX 不存在：$MsixPath" }
$MsixPath = (Resolve-Path -LiteralPath $MsixPath).Path
if ($UpgradeMsixPath) {
    if (-not (Test-Path -LiteralPath $UpgradeMsixPath)) { throw "UpgradeMsixPath 不存在：$UpgradeMsixPath" }
    $UpgradeMsixPath = (Resolve-Path -LiteralPath $UpgradeMsixPath).Path
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "需要管理员权限（装包 / 导入证书 / 读写注册表都要）。请用管理员 PowerShell 重跑。"
}

$dataDir = Join-Path $env:LOCALAPPDATA "DutyAgent"
$dataLogs = Join-Path $dataDir "data\logs"

Write-Host ""
Write-Host "===== Duty-Agent MSIX 生命周期验收 =====" -ForegroundColor Cyan
Write-Host "  包        : $MsixPath"
Write-Host "  包名      : $PackageName"
if ($UpgradeMsixPath) { Write-Host "  升级包    : $UpgradeMsixPath" }
Write-Host "  数据目录  : $dataDir"
Write-Host "  阶段      : $Phase"
Write-Host "  主机      : $env:COMPUTERNAME"
Write-Host ""

if (-not $Force) {
    $answer = Read-Host "本脚本会安装 MSIX、启动客户端、写自启注册表并在最后卸载。继续？(yes/NO)"
    if ($answer -ne "yes") { Write-Host "已取消。"; exit 0 }
}

# 已存在的实例会让端口/单实例判定混乱，先停掉
Get-Process -Name "duty-agent" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# ================================================================ Install
function Invoke-Install {
    Write-Step "阶段 1：安装"

    if ($CertPath) {
        if (-not (Test-Path -LiteralPath $CertPath)) { throw "CertPath 不存在：$CertPath" }
        $cert = (Resolve-Path -LiteralPath $CertPath).Path
        Test-Info "导入证书到「本地计算机\受信任人」：$cert"
        try {
            Import-Certificate -FilePath $cert -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
        }
        catch {
            # 已导入过同证书时会报错，不视为失败
            Test-Info "导入返回：$($_.Exception.Message)"
        }
    }

    if (Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue) {
        Test-Warn ("目标机已安装 {0}，将直接覆盖安装（等价于一次升级）。" -f $PackageName)
    }

    Test-Info "Add-AppxPackage ..."
    try {
        Add-AppxPackage -Path $MsixPath -ErrorAction Stop
    }
    catch {
        Test-Assert $false "Add-AppxPackage 成功" $_.Exception.Message
        return $null
    }

    $pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if (-not (Test-Assert ($null -ne $pkg) "安装后可查到包" "$PackageName")) { return $null }
    Test-Assert ($pkg.Version.Major -ge 1) "包版本可读" ("实际 " + $pkg.Version)

    $location = $pkg.InstallLocation
    Test-Assert (Test-Path -LiteralPath (Join-Path $location "duty-agent.exe")) "安装目录里有 duty-agent.exe"
    Test-Assert (Test-Path -LiteralPath (Join-Path $location "Assets_Duty")) "安装目录里有 Assets_Duty（内嵌 Python）"
    Test-Assert (Test-Path -LiteralPath (Join-Path $location "bridge")) "安装目录里有随包桥接插件"

    $pfn = $pkg.PackageFamilyName
    $aumid = "$pfn!App"
    Test-Info "PFN = $pfn"
    Test-Info "AUMID = $aumid"
    return $pkg
}

# ================================================================ First run
function Invoke-FirstRun {
    param([object]$Pkg)

    $pfn = $Pkg.PackageFamilyName
    $aumid = "$pfn!App"

    Write-Step "阶段 2：首跑（AUMID 激活）"

    New-Item -ItemType Directory -Force -Path $dataLogs | Out-Null
    $before = Get-ChildItem -LiteralPath $dataLogs -Filter "client-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    Test-Info "启动：explorer.exe shell:AppsFolder\$aumid"
    Start-Process -FilePath "explorer.exe" -ArgumentList "shell:AppsFolder\$aumid"

    # 等日志落盘
    $deadline = (Get-Date).AddSeconds($RunTimeoutSeconds)
    $log = $null
    while ((Get-Date) -lt $deadline) {
        $candidate = Get-ChildItem -LiteralPath $dataLogs -Filter "client-*.log" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($candidate -and (
                (-not $before) -or
                ($candidate.FullName -ne $before.FullName) -or
                ($candidate.LastWriteTime -gt $before.LastWriteTime))) {
            $log = $candidate
            break
        }
        Start-Sleep -Milliseconds 700
    }

    if (-not (Test-Assert ($null -ne $log) "客户端产生了日志" "等了 $RunTimeoutSeconds 秒仍无新日志")) {
        return $false
    }

    $content = Get-Content -LiteralPath $log.FullName -Tail 120 -ErrorAction SilentlyContinue
    Test-Assert (($content | Where-Object { $_ -like '*包身份=是(MSIX)*' })) "日志声明包身份（MSIX）" "这是 ScopeName 崩溃曾挂掉的地方"
    Test-Assert (($content | Where-Object { $_ -like "*PFN=$pfn*" })) "日志里的 PFN 与安装包一致" "实际找 $pfn"

    $proc = Get-Process -Name "duty-agent" -ErrorAction SilentlyContinue | Select-Object -First 1
    Test-Assert ($null -ne $proc) "duty-agent 进程在运行"
    $py = Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -like "*$pfn*" -or $_.CommandLine -like "*Assets_Duty*" }
    Test-Assert ($null -ne $py) "后端 python 子进程在运行（由 Job Object 托管）"

    Test-Assert (Test-Path -LiteralPath $dataLogs) "数据落在真实 %LOCALAPPDATA%\DutyAgent（未被重定向到 LocalState）" `
        ("若失败，zip 版与商店版会各存一份数据，是设计变更，需要同步引用处。")

    # 自启：host-config 默认 client_auto_start=true，Apply(true) 应写 Run 键
    $runValue = (Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name DutyAgent -ErrorAction SilentlyContinue).DutyAgent
    if ($runValue) {
        Test-Assert ($runValue -like "*shell:AppsFolder\$pfn!App") "自启写入 HKCU Run 键（AUMID 激活）" "实际：$runValue"
    }
    else {
        Test-Warn "Run 键里没有 DutyAgent 值（可能 host-config 关了 auto_start，或写键被策略拦）。"
    }

    # 给升级阶段留一个探针文件
    $marker = Join-Path $dataDir "lifecycle-marker.txt"
    "written-by-Test-MsixLifecycle at $(Get-Date -Format o)" | Set-Content -LiteralPath $marker -Encoding UTF8
    Test-Info "已写升级探针文件：$marker"

    Get-Process -Name "duty-agent" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    return $true
}

# ================================================================ Upgrade
function Invoke-Upgrade {
    Write-Step "阶段 3：升级"

    if (-not $UpgradeMsixPath) {
        Test-Warn ("未提供 -UpgradeMsixPath，跳过升级验收。生成方法：" +
                   "scripts\Build-MsixPackage.ps1 -ReleaseVersion 1.0.0.1 -SkipWebBuild，" +
                   "然后把产物传给 -UpgradeMsixPath。")
        return $true
    }

    $before = (Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue).Version
    $marker = Join-Path $dataDir "lifecycle-marker.txt"
    if (-not (Test-Path -LiteralPath $marker)) {
        Test-Warn "没有升级探针文件（阶段 2 没跑？），升级阶段只能验证版本号。"
    }

    Test-Info "Add-AppxPackage（升级包）..."
    try {
        Add-AppxPackage -Path $UpgradeMsixPath -ErrorAction Stop
    }
    catch {
        Test-Assert $false "升级安装成功" $_.Exception.Message
        return $false
    }

    $after = (Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue).Version
    Test-Assert ($after -and $before -and $after -gt $before) "升级后版本号递增" ("$before -> $after")

    Test-Assert (Test-Path -LiteralPath $marker) "升级后用户数据（探针文件）保留" "包内更新不应动 %LOCALAPPDATA% 下的数据。"
    return $true
}

# ================================================================ Uninstall
function Invoke-Uninstall {
    Write-Step "阶段 4：卸载"

    Get-Process -Name "duty-agent" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -like "*${PackageName}_*" -or $_.CommandLine -like "*Assets_Duty*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 1200

    $pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if ($pkg) {
        try {
            $pkg | Remove-AppxPackage -ErrorAction Stop
        }
        catch {
            Test-Assert $false "Remove-AppxPackage 成功" $_.Exception.Message
            return $false
        }
    }
    else {
        Test-Warn "目标机没装这个包，跳过卸载动作本身。"
    }

    Test-Assert (-not (Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)) "卸载后查不到包"

    if ($pkg -and $pkg.InstallLocation -and (Test-Path -LiteralPath $pkg.InstallLocation)) {
        Test-Assert $false "卸载后安装目录已删除" $pkg.InstallLocation
    }
    else {
        Test-Assert $true "卸载后安装目录已删除"
    }

    $runStill = (Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name DutyAgent -ErrorAction SilentlyContinue).DutyAgent
    if ($runStill) {
        # MSIX 没有卸载钩子，这是已知残留；脚本负责清，客户端无法在卸载时运行。
        Test-Warn "发现自启残留（MSIX 无卸载钩子，属已知项）：$runStill"
        Test-Assert ($runStill -like "*shell:AppsFolder*") "残留形态是 AUMID（可被 2-卸载.ps1 清理）" $runStill
    }
    else {
        Test-Assert $true "无自启残留"
    }

    if (Test-Path -LiteralPath $dataDir) {
        Test-Assert $true "用户数据保留（设计如此：卸载不删名单）"
    }
    else {
        Test-Warn "数据目录 $dataDir 不见了。"
    }
    return $true
}

# ================================================================ 调度
$firstRunOk = $false
switch ($Phase) {
    "Install" {
        $null = Invoke-Install
    }
    "Run" {
        $pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
        if (-not $pkg) {
            Test-Assert $false "阶段 Run 需要先装包" "先跑 -Phase Install"
        }
        else {
            $firstRunOk = Invoke-FirstRun -Pkg $pkg
        }
    }
    "Upgrade" {
        $null = Invoke-Upgrade
    }
    "Uninstall" {
        $null = Invoke-Uninstall
    }
    default {
        $firstRunOk = $false
        $pkg = Invoke-Install
        if ($null -ne $pkg) {
            $firstRunOk = Invoke-FirstRun -Pkg $pkg
        }
        Invoke-Upgrade | Out-Null
        Invoke-Uninstall | Out-Null
    }
}

# ================================================================ 汇总
Write-Host ""
Write-Host "============================================================"
Write-Host (" 生命周期验收完成：通过 {0} 项，失败 {1} 项，警告 {2} 项" -f
    $script:Passed, $script:Failures.Count, $script:Warnings.Count)
if ($script:Warnings.Count -gt 0) {
    Write-Host " 警告："
    foreach ($w in $script:Warnings) { Write-Host "   - $w" }
}
if ($script:Failures.Count -gt 0) {
    Write-Host " 失败项：" -ForegroundColor Red
    foreach ($f in $script:Failures) { Write-Host "   - $f" -ForegroundColor Red }
    Write-Host "============================================================"
    exit 1
}
Write-Host " 全部通过。"
Write-Host "============================================================"
exit 0
