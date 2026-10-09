<#
.SYNOPSIS
    一键构建 AutoCardCopier 解决方案。
.DESCRIPTION
    自动定位 MSBuild（优先使用 Visual Studio BuildTools），构建解决方案，
    产物统一输出到 build\out\<Configuration>\ 目录。
.PARAMETER Configuration
    Debug 或 Release，默认 Release。
.PARAMETER Clean
    构建前先清理 obj 与 out 目录。
.EXAMPLE
    .\build\build.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]\$Configuration = 'Release',
    [switch]\$Clean
)

\$ErrorActionPreference = 'Stop'
\$repoRoot = Split-Path -Parent \$PSScriptRoot
\$solution = Join-Path \$repoRoot 'src\AutoCardCopier.sln'

function Find-MSBuild {
    \$candidates = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach (\$path in \$candidates) {
        if (Test-Path -LiteralPath \$path) { return \$path }
    }
    # 兜底：在 Visual Studio 安装目录中搜索
    foreach (\$root in @('C:\Program Files (x86)\Microsoft Visual Studio', 'C:\Program Files\Microsoft Visual Studio')) {
        if (-not (Test-Path -LiteralPath \$root)) { continue }
        \$found = Get-ChildItem -LiteralPath \$root -Recurse -Depth 6 -Filter 'MSBuild.exe' -ErrorAction SilentlyContinue |
            Where-Object { \$_.FullName -match '\\MSBuild\\Current\\Bin\\MSBuild.exe$' } |
            Select-Object -First 1
        if (\$found) { return \$found.FullName }
    }
    throw '未能找到 MSBuild.exe，请安装 Visual Studio Build Tools。'
}

if (\$Clean) {
    Write-Host '清理构建产物…' -ForegroundColor Yellow
    Get-ChildItem -Path (Join-Path \$repoRoot 'src') -Recurse -Directory -Include 'obj', 'bin' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path \$repoRoot 'build\out') -ErrorAction SilentlyContinue
}

\$msbuild = Find-MSBuild
Write-Host \"使用 MSBuild：\$msbuild\" -ForegroundColor DarkGray
Write-Host \"配置：\$Configuration\" -ForegroundColor DarkGray

& \$msbuild \$solution /p:Configuration=\$Configuration /nologo /v:m /clp:Summary
if (\$LASTEXITCODE -ne 0) {
    Write-Host \"构建失败，退出码 \$LASTEXITCODE\" -ForegroundColor Red
    exit \$LASTEXITCODE
}

Write-Host \"构建成功，产物目录：\$repoRoot\build\out\\\$Configuration\" -ForegroundColor Green
