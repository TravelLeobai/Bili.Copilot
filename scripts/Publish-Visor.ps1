# Copyright (c) Bili Copilot. All rights reserved.

<#
.SYNOPSIS
    重新生成 Assets\visor 下的 BiliCopilot.Visor.exe（x64 + arm64）。

.DESCRIPTION
    应用的滚轮增强是独立进程，主程序只按路径启动 Assets\visor\<arch>\BiliCopilot.Visor.exe，
    所以换了 TFM 或改了 Visor 代码之后，必须重新发布这两个 exe 并覆盖进仓库，否则应用跑的还是旧二进制。

    默认走自包含单文件（不需要 C++ 链接器，任何装了 .NET 11 SDK 的机器都能跑）：
      x64   ≈ 14 MB
      arm64 ≈ 15 MB
    加 -Aot 则走 Native AOT（≈ 3 MB，体积只有前者的四分之一），
    但需要先装 Visual Studio Build Tools 的 C++ 桌面工作负载 + Windows SDK，
    否则会报 "Platform linker not found"。

.PARAMETER Aot
    Native AOT 发布（需要 MSVC 链接器）。

.PARAMETER OutputRoot
    中间产物目录，默认放到系统临时目录下的 visor-publish。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Visor.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Visor.ps1 -Aot
#>
[CmdletBinding()]
param(
    [switch]$Aot,
    [string]$OutputRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'visor-publish')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Desktop\BiliCopilot.Visor\BiliCopilot.Visor.csproj'
$assets = Join-Path $repoRoot 'src\Desktop\BiliCopilot.UI\Assets\visor'

if (-not (Test-Path $project)) {
    throw "找不到 Visor 工程：$project"
}

# 自包含单文件：不需要链接器；AOT 才需要 MSVC.
$commonArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-p:PublishSingleFile=true',
    '-p:SelfContained=true',
    '-p:PublishTrimmed=true'
)

if ($Aot) {
    $commonArgs += @('-p:PublishAot=true')
    Write-Host '发布模式：Native AOT（需要 MSVC 链接器）'
}
else {
    $commonArgs += @('-p:PublishAot=false')
    Write-Host '发布模式：自包含单文件（不需要链接器）'
}

$targets = @(
    @{ Rid = 'win-x64'; Platform = 'x64'; Dest = 'x64' },
    @{ Rid = 'win-arm64'; Platform = 'arm64'; Dest = 'arm64' }
)

foreach ($target in $targets) {
    $out = Join-Path $OutputRoot $target.Dest
    if (Test-Path $out) {
        Remove-Item $out -Recurse -Force
    }

    Write-Host "发布 $($target.Rid) ..."
    & dotnet @commonArgs -r $target.Rid "-p:Platform=$($target.Platform)" -o $out
    if ($LASTEXITCODE -ne 0) {
        throw "发布 $($target.Rid) 失败（退出码 $LASTEXITCODE）。若报 Platform linker not found，说明缺 MSVC，改用不带 -Aot 的默认模式。"
    }

    $source = Join-Path $out 'BiliCopilot.Visor.exe'
    if (-not (Test-Path $source)) {
        throw "发布产物里没有 BiliCopilot.Visor.exe：$out"
    }

    $destination = Join-Path $assets "$($target.Dest)\BiliCopilot.Visor.exe"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item $source $destination -Force

    $size = [math]::Round((Get-Item $destination).Length / 1KB, 0)
    Write-Host "  已覆盖 $destination（$size KB）"
}

Write-Host '完成。记得把 Assets\visor 下两个 exe 一起提交。'
