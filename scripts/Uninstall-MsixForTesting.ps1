#Requires -RunAsAdministrator
<#
    .SYNOPSIS
        Duty-Agent MSIX 测试包 —— 卸载 + 清理残留（需管理员）。
    .DESCRIPTION
        停进程 → 移除包 → 清 Run 键自启残留 → 报告数据/证书状态。

        Run 键这块是重点，也是以前错的地方：
        包身份下客户端写的是 **DutyAgent.<PFN>**（AutoStartManager 的 ScopedRunValueName），
        裸值名 `DutyAgent` 是 zip 版在用的。以前这里只删裸名，等于没清；
        现在按 PFN 作用域精确删，并且**只在裸名的值确实是本包 AUMID 命令时才删**，
        避免把同机 zip 版的自启一起带走。
    .PARAMETER PackageName
        清单 Identity.Name。不给则从仓库的 Package.appxmanifest 读。
    .PARAMETER RemoveCertificate
        连开发证书一起从「本地计算机\受信任人」移除。默认保留，方便反复安装。
    .EXAMPLE
        Set-ExecutionPolicy -Scope Process Bypass -Force
        .\Uninstall-MsixForTesting.ps1
#>
[CmdletBinding()]
param(
    [string]$PackageName = "",
    [switch]$RemoveCertificate
)

$ErrorActionPreference = 'Continue'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Get-ManifestIdentity {
    $manifest = Join-Path (Split-Path -Parent $PSScriptRoot) 'DutyAgent.Client.Package\Package.appxmanifest'
    if (-not (Test-Path -LiteralPath $manifest)) { return $null }
    try {
        [xml]$doc = [System.IO.File]::ReadAllText($manifest, [System.Text.Encoding]::UTF8)
    } catch {
        return $null
    }
    $nsm = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $nsm.AddNamespace('x', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $doc.SelectSingleNode('/x:Package/x:Identity', $nsm)
    if ($null -eq $identity) { return $null }
    return [pscustomobject]@{
        Name      = $identity.GetAttribute('Name')
        Publisher = $identity.GetAttribute('Publisher')
    }
}

Write-Host ''
Write-Host '===== Duty-Agent MSIX 测试包：卸载 =====' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------- 0/4 解析身份
$manifestIdentity = Get-ManifestIdentity
if (-not $PackageName) {
    if ($manifestIdentity) {
        $PackageName = $manifestIdentity.Name
    } else {
        throw '没给 -PackageName，也读不到 Package.appxmanifest。请显式指定包名。'
    }
}
Write-Host ("目标包名 : {0}" -f $PackageName)

$pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
$pfn = $null
if ($pkg) {
    $pfn = $pkg.PackageFamilyName
    Write-Host ("已安装   : {0}（{1}）" -f $pkg.Version, $pfn)
} else {
    Write-Host '已安装   : 否（仍会清理残留）'
}
Write-Host ''

# ---------------------------------------------------------------- 1/4 停进程
Write-Host '[1/4] 关闭正在运行的客户端…'
Get-Process -Name 'duty-agent' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
# 后端 python 子进程由 Job Object 连带回收；这里再兜一次底（只动本包安装目录下的）
Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -like "*WindowsApps*${PackageName}_*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 1200
Write-Host '      完成。' -ForegroundColor Green

# ---------------------------------------------------------------- 2/4 移除包
Write-Host '[2/4] 移除应用包…'
if ($pkg) {
    $pkg | Remove-AppxPackage
    if (Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue) {
        Write-Warning '      卸载命令执行了，但仍能查到包。可能有其他用户会话占用，或需要重启后重试。'
    } else {
        Write-Host '      已移除。' -ForegroundColor Green
    }
} else {
    Write-Host '      未安装，跳过。'
}

# ---------------------------------------------------------------- 3/4 Run 键残留
Write-Host '[3/4] 清理自启残留（HKCU\...\Run）…'
$removed = @()
$kept = @()
$props = Get-ItemProperty -Path $runKeyPath -ErrorAction SilentlyContinue
if ($props) {
    $candidates = @($props.PSObject.Properties |
        Where-Object { $_.Name -like 'DutyAgent*' } |
        ForEach-Object { $_.Name })

    foreach ($name in $candidates) {
        $value = [string]$props.$name

        # 本包 PFN 作用域值：精确匹配
        $isScopedExact = $pfn -and ($name -eq "DutyAgent.$pfn")
        # 包已卸载、PFN 拿不到时按前缀兜底：DutyAgent.<Name>_<hash>
        $isScopedByPrefix = $name -like "DutyAgent.${PackageName}_*"

        # 裸值名：只有"确实是本包 AUMID 命令"才删，zip 版的 exe 路径必须留着
        $isLegacyOurs = $false
        if ($name -eq 'DutyAgent') {
            if ($pfn) {
                $isLegacyOurs = ($value -eq "explorer.exe shell:AppsFolder\$pfn!App")
            } else {
                $isLegacyOurs = $value -like 'explorer.exe shell:AppsFolder\*!App'
            }
        }

        if ($isScopedExact -or $isScopedByPrefix -or $isLegacyOurs) {
            Remove-ItemProperty -Path $runKeyPath -Name $name -ErrorAction SilentlyContinue
            $removed += $name
        } else {
            $kept += ("{0} = {1}" -f $name, $value)
        }
    }
}

if ($removed.Count -gt 0) {
    Write-Host ("      已清理：{0}" -f ($removed -join ', ')) -ForegroundColor Green
} else {
    Write-Host '      没有需要清理的自启项。'
}
if ($kept.Count -gt 0) {
    Write-Host '      保留（不属于本包，多半是 zip 版）：' -ForegroundColor Yellow
    foreach ($k in $kept) { Write-Host ("        {0}" -f $k) -ForegroundColor Yellow }
}

# ---------------------------------------------------------------- 4/4 数据与证书
Write-Host '[4/4] 用户数据与证书：'
$data = Join-Path $env:LOCALAPPDATA 'DutyAgent'
if (Test-Path -LiteralPath $data) {
    Write-Host ("      数据仍保留：{0}" -f $data) -ForegroundColor Yellow
    Write-Host  '      （设计如此：卸载不删用户数据，避免误删排班名单。需要时手动删。）'
} else {
    Write-Host '      未发现 %LOCALAPPDATA%\DutyAgent。'
}

$publisher = if ($manifestIdentity) { $manifestIdentity.Publisher } else { '' }
if ($RemoveCertificate) {
    if (-not $publisher) {
        Write-Warning '      读不到 Publisher，无法定位证书；跳过。'
    } else {
        $normalize = { param($s) ($s -replace '\s', '') }
        $target = & $normalize $publisher
        $certs = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
            Where-Object { (& $normalize $_.Subject) -eq $target })
        if ($certs.Count -gt 0) {
            $certs | Remove-Item -Force -ErrorAction SilentlyContinue
            Write-Host ("      已移除开发证书 {0} 张（{1}）。" -f $certs.Count, $publisher) -ForegroundColor Green
        } else {
            Write-Host '      「受信任人」里没有对应证书。'
        }
    }
} else {
    if ($publisher) {
        Write-Host ("      开发证书保留在「受信任人」中（subject {0}），方便反复安装。" -f $publisher)
    }
    Write-Host '      要一并移除，加 -RemoveCertificate 重跑。'
}

Write-Host ''
Write-Host '卸载完成。' -ForegroundColor Green
Write-Host ''
