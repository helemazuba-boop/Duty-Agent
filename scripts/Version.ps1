<#
.SYNOPSIS
    Duty-Agent 的三段版本号工具：读取、指纹判定、自增。

.DESCRIPTION
    版本策略（契约 C3 的扩展）：
      * 对外只有三段：<major>.<minor>.<build>
      * major/minor 由人手动改 Assets_Duty\version.py 的 APP_VERSION
      * build 段由构建脚本（-AutoVersion）自动 +1
      * MSIX Identity.Version 由 Build-MsixPackage 补成四段
        <major>.<minor>.<build>.0（商店要求第四段恒为 0）

    幂等的关键：是否自增按**源码指纹**判定，不按"又构建了一次"判定。
    同一份代码连构建 N 次版本不变，产物才能稳定对应一个提交。
    指纹 = HEAD + 工作区 status/diff，但排除构建自己会写的文件
    （version.py / README.md）和前端构建产物（Assets_Duty\web），
    否则"构建改了 version.py"会被当成"源码变了"，版本号一路狂奔。

    手改前两位：把 APP_VERSION 改成 1.1.0 即可，构建时不会再加一
    （major/minor 变化视为主动切版本），之后从 1.1.1 起继续自动递增。

    本文件只定义函数，由构建脚本 dot-source 使用，不直接执行。
#>

function Read-VersionFileText {
    param([Parameter(Mandatory = $true)][string]$Path)

    # UTF-8 显式读：version.py / README / csproj 都是 BOM-less UTF-8，
    # Get-Content 在 PS 5.1 下会按 ANSI 解，中文直接变乱码。
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

function Write-VersionFileText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}

function Get-AppVersionFromPyFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "找不到版本单一事实源：$Path"
    }

    $text = Read-VersionFileText -Path $Path
    $match = [regex]::Match($text, '(?m)^APP_VERSION[ \t]*=[ \t]*"([^"\r\n]*)"')
    if (-not $match.Success) {
        throw "$Path 里没有 APP_VERSION 赋值行。"
    }

    return $match.Groups[1].Value
}

function Set-AppVersionInPyFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $text = Read-VersionFileText -Path $Path
    $pattern = '(?m)^APP_VERSION[ \t]*=[ \t]*"[^"\r\n]*"'
    $existing = [regex]::Matches($text, $pattern)
    if ($existing.Count -ne 1) {
        throw "$Path 里 APP_VERSION 行数 = $($existing.Count)，期望恰好 1 行。"
    }

    # 只改引号里的值：注释、docstring、行尾风格全部保持原样。
    $newText = [regex]::Replace($text, $pattern, ('APP_VERSION = "{0}"' -f $Version))
    if ($newText -ne $text) {
        Write-VersionFileText -Path $Path -Text $newText
    }

    $check = Get-AppVersionFromPyFile -Path $Path
    if ($check -ne $Version) {
        throw "版本盖章校验失败：期望 $Version，实际 $check。"
    }
}

function Set-ReadmeVersionBadge {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $text = Read-VersionFileText -Path $Path
    # 只认"当前版本: v1.2.3"这一处，避免误伤正文里其它版本号。
    $pattern = '(当前版本[:：][ \t]*v?)(\d+\.\d+\.\d+)'
    if (-not [regex]::IsMatch($text, $pattern)) {
        Write-Warning "README 里没有 '当前版本: vX.Y.Z' 徽标，跳过同步。"
        return
    }

    # 用 Regex 实例的 Replace(text, replacement, count) 重载：
    # 静态重载在 PS 5.1 下会把 int 当 RegexOptions，静默变成"全部替换"。
    $regex = New-Object System.Text.RegularExpressions.Regex($pattern)
    $newText = $regex.Replace($text, ('${1}' + $Version), 1)
    if ($newText -ne $text) {
        Write-VersionFileText -Path $Path -Text $newText
    }
}

function Get-SourceFingerprint {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    # 构建自己会写的文件必须排除，否则"改版本号"会被当成"源码变了"，
    # 版本号会在每次构建后一路狂奔。
    $excludes = @(
        ':(exclude)Assets_Duty/version.py',
        ':(exclude)Assets_Duty/web',
        ':(exclude)README.md'
    )

    $head = @(& git -C $RepoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -eq 0 -or [string]::IsNullOrWhiteSpace($head[0])) {
        throw "取不到 git HEAD：$RepoRoot 不是 git 仓库？"
    }

    # 已提交内容用 ls-tree 的 blob 哈希来算（git ls-tree 不支持 exclude 魔法，
    # 这里自己按路径过滤）。这样"只提交了版本号"的 commit 不会改变指纹 ——
    # 否则每次提交版本号都会换来一次无意义的 +1。
    $treeLines = @(& git -C $RepoRoot ls-tree -r HEAD)
    $treePayload = @($treeLines | Where-Object {
            $tab = $_.IndexOf("`t")
            if ($tab -lt 0) { return $false }
            $path = $_.Substring($tab + 1)
            if ($path -eq 'Assets_Duty/version.py' -or $path -eq 'README.md') { return $false }
            if ($path.StartsWith('Assets_Duty/web/', [System.StringComparison]::Ordinal)) { return $false }
            return $true
        }) -join "`n"

    # 未提交的改动也要算进去：改完代码没提交就构建，版本同样要涨。
    $status = @(& git -C $RepoRoot status --porcelain --untracked-files=normal -- . @excludes)
    $diff = @(& git -C $RepoRoot --no-pager diff --binary -- . @excludes)

    $payload = $head[0].Trim() + "`n" + $treePayload + "`n" + ($status -join "`n") + "`n" + ($diff -join "`n")

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-VersionStamp {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $path = Join-Path $RepoRoot ".release\.version-stamp.json"
    if (-not (Test-Path -LiteralPath $path)) {
        return $null
    }

    try {
        $obj = (Read-VersionFileText -Path $path) | ConvertFrom-Json
        if (-not $obj.version -or -not $obj.fingerprint) {
            return $null
        }

        return $obj
    }
    catch {
        # 记录文件损坏只意味着"这一轮当作没构建过"，不值得让构建失败。
        return $null
    }
}

function Save-VersionStamp {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Fingerprint,
        [string]$Commit = ""
    )

    $dir = Join-Path $RepoRoot ".release"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $payload = [ordered]@{
        version     = $Version
        fingerprint = $Fingerprint
        commit      = $Commit
        updatedUtc  = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    Write-VersionFileText -Path (Join-Path $dir ".version-stamp.json") -Text ($payload | ConvertTo-Json)
}

function Compare-AppVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    $l = @($Left.Split('.') | ForEach-Object { [int]$_ })
    $r = @($Right.Split('.') | ForEach-Object { [int]$_ })
    for ($i = 0; $i -lt 3; $i++) {
        if ($l[$i] -ne $r[$i]) {
            if ($l[$i] -gt $r[$i]) { return 1 }
            return -1
        }
    }

    return 0
}

function Get-BridgeDeclaredVersion {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    # 桥接插件版本不跟随客户端自动递增：读它自己声明的值原样传下去。
    # 否则每构建一次客户端就把 ClassIsland 插件的版本顶高一次。
    $csproj = Join-Path $RepoRoot "Duty-Agent-ClassIsland-Bridge\DutyAgentBridge.csproj"
    if (-not (Test-Path -LiteralPath $csproj)) {
        throw "找不到桥接工程：$csproj"
    }

    $match = [regex]::Match((Read-VersionFileText -Path $csproj), '<Version>\s*([0-9][0-9.]*)\s*</Version>')
    if (-not $match.Success) {
        throw "桥接 csproj 里没有 <Version>：桥接不跟随客户端版本，必须能读到它自己的值。"
    }

    return $match.Groups[1].Value
}

function Step-AppVersion {
    <#
      算这一次构建该用哪个版本，必要时把 build 段 +1 写回 version.py 与 README。
      返回三段版本号字符串（如 1.0.7）。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [switch]$Force
    )

    $versionPy = Join-Path $RepoRoot "Assets_Duty\version.py"
    $readme = Join-Path $RepoRoot "README.md"
    $current = Get-AppVersionFromPyFile -Path $versionPy
    if ($current -notmatch '^\d+\.\d+\.\d+$') {
        throw "APP_VERSION '$current' 必须是三段 x.y.z：前两位手动，第三位自动。"
    }

    $stamp = Get-VersionStamp -RepoRoot $RepoRoot
    if ($stamp -and (Compare-AppVersion -Left $current -Right $stamp.version) -lt 0) {
        throw ("version.py 现在是 $current，比上次构建的 $($stamp.version) 还低。" +
            "MSIX 不允许降级安装（0x80073CF9），请把 APP_VERSION 改回 >= $($stamp.version)。")
    }

    $fingerprint = Get-SourceFingerprint -RepoRoot $RepoRoot
    $commit = (@(& git -C $RepoRoot rev-parse --short HEAD) -join "").Trim()

    $sameSource = $stamp -and ($stamp.fingerprint -eq $fingerprint)
    if ($sameSource -and -not $Force) {
        if ($stamp.version -ne $current) {
            Write-Host "[version] 手动切换版本：$($stamp.version) -> $current（源码未变，不再自增）"
        }
        else {
            Write-Host "[version] 源码自上次构建未变，沿用 $current（$commit）"
        }

        # 版本可能被手改过：顺手同步 README 与记录，别让文档和产物说两套话。
        Set-ReadmeVersionBadge -Path $readme -Version $current
        if ($stamp.version -ne $current) {
            Save-VersionStamp -RepoRoot $RepoRoot -Version $current -Fingerprint $fingerprint -Commit $commit
        }

        return $current
    }

    $parts = @($current.Split('.') | ForEach-Object { [int]$_ })
    $manualMajorMinor = $stamp -and ($parts[0] -ne [int]$stamp.version.Split('.')[0] -or $parts[1] -ne [int]$stamp.version.Split('.')[1])

    if ($manualMajorMinor) {
        # 手改了 major/minor：这就是新版本号本身，不要再 +1，
        # 否则刚改成 1.1.0 的第一次构建会变成 1.1.1。
        $next = $current
        Write-Host "[version] 检测到手动切换版本：$($stamp.version) -> $current（本轮不再自增）"
    }
    else {
        if ($parts[2] -ge 65535) {
            throw ("build 段已达 MSIX 单段上限 65535，请手动把 minor +1（例如 " +
                "$($parts[0]).$($parts[1] + 1).0）。")
        }

        $next = '{0}.{1}.{2}' -f $parts[0], $parts[1], ($parts[2] + 1)
        Write-Host "[version] $current -> $next（源码有变更，$commit）"
    }

    Set-AppVersionInPyFile -Path $versionPy -Version $next
    Set-ReadmeVersionBadge -Path $readme -Version $next
    Save-VersionStamp -RepoRoot $RepoRoot -Version $next -Fingerprint $fingerprint -Commit $commit
    return $next
}
