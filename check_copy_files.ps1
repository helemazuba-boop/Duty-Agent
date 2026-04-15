# 副本文件检查脚本
# 用于扫描仓库中OneDrive生成的"副本"文件

$ErrorActionPreference = "Continue"

Write-Host "=== 副本文件扫描工具 ===" -ForegroundColor Cyan
Write-Host "扫描目录: $PSScriptRoot" -ForegroundColor Yellow
Write-Host ""

# 要排除的目录（系统/工具目录）
$excludeDirs = @(
    '\.git',
    '\.dotnet',
    '\.venv',
    'node_modules',
    '\.pytest_cache',
    '\.tmp',
    'agent-tools',
    'bin',
    'obj',
    '\.net'
)

# 查找所有包含" - 副本"的文件
Write-Host "正在扫描..." -ForegroundColor Yellow
$allCopyFiles = Get-ChildItem -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "* - 副本*" }

Write-Host "找到总数: $($allCopyFiles.Count) 个文件" -ForegroundColor Yellow
Write-Host ""

# 分类统计
$byDirectory = $allCopyFiles | Group-Object DirectoryName | Sort-Object Count -Descending

Write-Host "=== 按目录分类 ===" -ForegroundColor Green
foreach ($group in $byDirectory) {
    $dirDisplay = $group.Name
    if ($dirDisplay -like "*\.git*") {
        $dirDisplay = "[Git目录] $dirDisplay"
    }
    Write-Host "  $dirDisplay : $($group.Count) 个文件"
}
Write-Host ""

# 列出所有文件
Write-Host "=== 完整文件列表 ===" -ForegroundColor Green
foreach ($file in $allCopyFiles) {
    $fileType = switch ($file.Extension) {
        ".py" { "[Python]" }
        ".cs" { "[C#]" }
        ".json" { "[JSON]" }
        ".dll" { "[DLL]" }
        ".exe" { "[EXE]" }
        ".pyc" { "[Python缓存]" }
        ".pyd" { "[Python模块]" }
        default { "[文件]" }
    }
    Write-Host "  $fileType $($file.FullName)"
}
Write-Host ""

# 统计各类型文件
$byExtension = $allCopyFiles | Group-Object Extension | Sort-Object Count -Descending
Write-Host "=== 按文件类型 ===" -ForegroundColor Green
foreach ($extGroup in $byExtension) {
    Write-Host "  $($extGroup.Name) : $($extGroup.Count) 个"
}
Write-Host ""

# 用户确认删除
Write-Host "=== 删除操作 ===" -ForegroundColor Green
$userFiles = $allCopyFiles | Where-Object {
    $_.FullName -notmatch "\.git\\|\\.dotnet\\|\\.venv\\|node_modules\\|\.pytest_cache\\|\.tmp\\|agent-tools\\|bin\\|obj"
}

Write-Host "用户项目文件数: $($userFiles.Count)" -ForegroundColor Yellow
Write-Host "系统文件数: $($allCopyFiles.Count - $userFiles.Count)" -ForegroundColor Yellow

$response = Read-Host "`n是否要删除所有副本文件？(y/n)"
if ($response -eq 'y' -or $response -eq 'Y') {
    Write-Host "正在删除..." -ForegroundColor Red
    $userFiles | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "删除完成！" -ForegroundColor Green

    # 验证
    $remaining = Get-ChildItem -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "* - 副本*" } |
        Where-Object { $_.FullName -notmatch "\.git\\|\\.dotnet\\|\\.venv\\|node_modules\\|\.pytest_cache\\|\.tmp\\|agent-tools\\|bin\\|obj" }

    if ($remaining) {
        Write-Host "警告: 仍有 $($remaining.Count) 个副本文件未删除" -ForegroundColor Red
    } else {
        Write-Host "✓ 所有副本文件已清理完毕" -ForegroundColor Green
    }
} else {
    Write-Host "已取消删除操作" -ForegroundColor Yellow
}

Write-Host "`n=== 扫描完成 ===" -ForegroundColor Cyan
