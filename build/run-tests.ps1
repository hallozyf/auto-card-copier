<#
.SYNOPSIS
    构建并运行全部自动化测试。
.DESCRIPTION
    先调用 build.ps1 构建（默认 Release），再运行测试可执行文件，
    以测试进程的退出码作为脚本退出码（0 表示全部通过）。
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$testExe = Join-Path $repoRoot ('build\out\' + $Configuration + '\AutoCardCopier.Tests.exe')
if (-not (Test-Path -LiteralPath $testExe)) {
    Write-Host "未找到测试程序: $testExe" -ForegroundColor Red
    exit 1
}

& $testExe
exit $LASTEXITCODE
