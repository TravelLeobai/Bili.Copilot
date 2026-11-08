# 重新注册 dev 包并保留应用数据（登录态、设置）。
#
# 背景：这个项目用 dev 模式注册（Add-AppxPackage -Register）直接跑 bin 目录。
# 重编译之后，包激活偶尔会坏掉，表现为启动即崩（事件日志 combase.dll + 0x80004003，
# 日志停在 "Visor initialized successfully"）。已装状态下同版本注册会被拒（0x80073CFB），
# 提版本号升级注册（0x80073D06 反向拒绝）也不总能解决，只有先卸载再注册最可靠 ——
# 但卸载会把 %LOCALAPPDATA%\Packages\<包名>\LocalState 一起删掉，登录态和设置跟着没。
#
# 所以这个脚本做四件事：备份 LocalState → 卸载 → 注册 → 还原 LocalState → 启动。
#
# 用法：pwsh -File tools\run-app.ps1 [-Bin <bin 目录>] [-NoBuild]

param(
    [string]$Bin = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$packageName = "Richasy.BiliCopilot.Dev"
$aumid = "Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App"

if ([string]::IsNullOrEmpty($Bin)) {
    $Bin = Join-Path $repo "src\_build\x64\Debug\BiliCopilot.UI\bin"
}

if (-not $NoBuild) {
    Write-Host "== 构建 ==" -ForegroundColor Cyan
    Push-Location (Join-Path $repo "src")
    try {
        & D:\dotnet\dotnet.exe build "Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj" -c Debug -p:Platform=x64 "-p:SolutionDir=$repo\src\"
        if ($LASTEXITCODE -ne 0) {
            throw "构建失败（exit $LASTEXITCODE）"
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host "== 停掉旧进程 ==" -ForegroundColor Cyan
Get-Process -Name "*BiliCopilot*" -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }
Start-Sleep -Seconds 4

# 包数据目录名带 publisher hash（Richasy.BiliCopilot.Dev_g7ew4fxhv1f46），
# 不能直接拼包名，否则 Test-Path 永远 false、备份从来没跑过.
$packageFamily = (Get-AppxPackage -Name $packageName).PackageFamilyName
$packageDir = Join-Path $env:LOCALAPPDATA "Packages\$packageFamily"
$backup = Join-Path $env:TEMP "bilicopilot-data-backup"

# 登录态和设置其实在包根目录的 Settings\settings.dat 里（不在 LocalState），
# 所以三个目录都要一起备份，只备份 LocalState 会丢登录.
$dataDirs = @("LocalState", "Settings", "RoamingState")
if (Test-Path $packageDir) {
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($dir in $dataDirs) {
        $src = Join-Path $packageDir $dir
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $backup $dir) -Recurse -Force
        }
    }

    Write-Host "== 已备份 LocalState / Settings / RoamingState → $backup ==" -ForegroundColor Cyan
}

Write-Host "== 卸载 dev 包 ==" -ForegroundColor Cyan
Get-AppxPackage -Name $packageName | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
Start-Sleep -Seconds 3

Write-Host "== 重新注册 ==" -ForegroundColor Cyan
Add-AppxPackage -Register (Join-Path $Bin "AppxManifest.xml")
Start-Sleep -Seconds 2

if (Test-Path $backup) {
    foreach ($dir in $dataDirs) {
        $saved = Join-Path $backup $dir
        if (Test-Path $saved) {
            Copy-Item $saved (Join-Path $packageDir $dir) -Recurse -Force
        }
    }

    Write-Host "== 已还原应用数据（登录态应当还在） ==" -ForegroundColor Cyan
}

Write-Host "== 启动 ==" -ForegroundColor Cyan
Start-Process "shell:AppsFolder\$aumid"
Start-Sleep -Seconds 26

$app = Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue
if ($app) {
    Write-Host "OK：进程 PID=$($app.Id)" -ForegroundColor Green
}
else {
    Write-Host "启动失败 —— 去看事件查看器里 combase.dll 那条" -ForegroundColor Red
    exit 1
}


