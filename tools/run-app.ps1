# 编译 + 注册 + 启动（默认不清应用数据）。
#
# 背景：这个项目用 dev 模式注册（Add-AppxPackage -Register）直接跑 bin 目录。
#   * 同版本重注册会被拒（0x80073CFB）；
#   * 卸载再注册能解决，但会把 %LOCALAPPDATA%\Packages\<包族> 下的 LocalState / Settings 一起删掉，
#     登录态、设置全没（Settings\settings.dat 才是登录态所在）。
#   * 实测把备份的 settings.dat 放回去会启动即崩（combase 0x80004003），所以默认不还原设置。
#
# 现在的做法：每次构建前把清单版本号 +1，然后 Add-AppxPackage -Register 走"升级注册"，
# 不卸载、数据保留。启动失败就重试几次（重新注册后第一次启动偶发失败）。
#
# 用法：
#   pwsh -File tools\run-app.ps1              # 构建 + 注册 + 启动
#   pwsh -File tools\run-app.ps1 -NoBuild     # 只注册 + 启动
#   pwsh -File tools\run-app.ps1 -CleanData   # 先用卸载再注册（会清数据，兜底用）

param(
    [string]$Bin = "",
    [switch]$NoBuild,
    [switch]$CleanData
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$packageName = "Richasy.BiliCopilot.Dev"
$aumid = "Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App"
$manifestSource = Join-Path $repo "src\Desktop\BiliCopilot.UI\Package.appxmanifest"

if ([string]::IsNullOrEmpty($Bin)) {
    $Bin = Join-Path $repo "src\_build\x64\Debug\BiliCopilot.UI\bin"
}

$binManifest = Join-Path $Bin "AppxManifest.xml"

function Get-ManifestVersion([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $text = Get-Content $path -Raw
    if ($text -match 'Version="(\d+)\.(\d+)\.(\d+)\.(\d+)"') {
        return [version]("{0}.{1}.{2}.{3}" -f $matches[1], $matches[2], $matches[3], $matches[4])
    }
    return $null
}

function Set-ManifestVersion([string]$path, [version]$version) {
    $text = Get-Content $path -Raw
    $text = [regex]::Replace($text, 'Version="\d+\.\d+\.\d+\.\d+"', ('Version="{0}"' -f $version), 1)
    Set-Content -Path $path -Value $text -Encoding utf8
}

Write-Host "== 停掉旧进程 ==" -ForegroundColor Cyan
Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Start-Sleep -Seconds 5
Get-Process -Name "*BiliCopilot*" -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }
Start-Sleep -Seconds 3

if (-not $NoBuild) {
    # 构建前先加版本号：构建会把清单从源码复制到 bin，所以改源码这份.
    $current = Get-ManifestVersion $manifestSource
    if ($null -ne $current) {
        $next = [version]("{0}.{1}.{2}.{3}" -f $current.Major, $current.Minor, $current.Build, ($current.Revision + 1))
        Set-ManifestVersion $manifestSource $next
        Write-Host "== 版本号 $current → $next ==" -ForegroundColor Cyan
    }

    Write-Host "== 构建 ==" -ForegroundColor Cyan
    Push-Location (Join-Path $repo "src")
    try {
        & D:\dotnet\dotnet.exe build "Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj" -c Debug -p:Platform=x64 "-p:SolutionDir=$repo\src\"
        if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }
    }
    finally {
        Pop-Location
    }
}

if ($CleanData) {
    Write-Host "== 卸载 dev 包（会清应用数据） ==" -ForegroundColor Yellow
    Get-AppxPackage -Name $packageName | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
    Start-Sleep -Seconds 3
}

Write-Host "== 注册 ==" -ForegroundColor Cyan
try {
    Add-AppxPackage -Register $binManifest
}
catch {
    $message = $_.Exception.Message
    Write-Host "注册失败（$message），改用卸载 + 注册（会清数据）" -ForegroundColor Yellow
    Get-AppxPackage -Name $packageName | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
    Start-Sleep -Seconds 3
    Add-AppxPackage -Register $binManifest
}

Start-Sleep -Seconds 2
Write-Host "== 启动 ==" -ForegroundColor Cyan
for ($i = 1; $i -le 4; $i++) {
    Start-Process "shell:AppsFolder\$aumid"
    Start-Sleep -Seconds 26
    $app = Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
    if ($app) {
        Write-Host "OK：进程 PID=$($app.Id)" -ForegroundColor Green
        exit 0
    }

    Write-Host "第 $i 次没起来，重试" -ForegroundColor Yellow
    Get-Process -Name "*BiliCopilot*" -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }
    Start-Sleep -Seconds 4
}

Write-Host "启动失败 —— 看事件查看器里 combase.dll 那条；可加 -CleanData 重注册（会清数据）" -ForegroundColor Red
exit 1
