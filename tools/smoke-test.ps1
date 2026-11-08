# Bili.Copilot 构建后启动验证（Smoke Test）
#
# 目的：不再拿 "dotnet build 成功" 当完成标准。这个脚本真的去启动 WinUI 桌面程序，
# 用应用自己写的 startup-ok.txt 判断是否走到"窗口首次激活"，然后关闭、再启动一次，
# 最后用 UIA 检查主窗口里有没有导航项。
#
# 退出码（任何一种失败都必须非零）：
#   0  全部通过
#   2  构建失败
#   3  部署 / 包注册失败（或注册的不是当前构建产物）
#   4  第一次启动失败（进程提前退出 / 超时 / 没等到 startup-ok）
#   5  第二次启动失败（关掉再开一次失败 —— 就是"重开打不开"那类问题）
#   6  UI 无响应或 UIA 检查未通过
#   7  测试环境问题（找不到包 / 找不到产物 / 权限）
#
# 安全约束：
#   * 不删除、不移动 Settings / LocalState，不重装应用（除非显式 -Reinstall）。
#   * 失败时保留全部现场：构建日志、启动日志、事件日志片段、结果 JSON。
#
# 用法：
#   pwsh -File tools\smoke-test.ps1                 # 构建 + 注册 + 两次启动 + UIA
#   pwsh -File tools\smoke-test.ps1 -NoBuild        # 用现有产物
#   pwsh -File tools\smoke-test.ps1 -Reinstall      # 显式允许卸载重装（会清应用数据）

param(
    [switch]$NoBuild,
    [switch]$Reinstall,
    [int]$LaunchTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$packageName = "Richasy.BiliCopilot.Dev"
$aumid = "Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App"
$bin = Join-Path $repo "src\_build\x64\Debug\BiliCopilot.UI\bin"
$binExe = Join-Path $bin "BiliCopilot.UI.exe"
$pf = (Get-AppxPackage -Name $packageName 2>$null).PackageFamilyName
$traceRoots = @(
    (Join-Path $bin "startup-trace"),
    (Join-Path $env:LOCALAPPDATA "Packages\$pf\LocalState\StartupTrace"),
    (Join-Path $env:TEMP "BiliCopilot.Startup")
)
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$resultDir = Join-Path $repo "tools\smoke-results\$stamp"
New-Item -ItemType Directory -Path $resultDir -Force | Out-Null

$result = [ordered]@{
    timestamp   = (Get-Date).ToString("O")
    build       = "skipped"
    deploy      = "pending"
    launch1     = "pending"
    launch2     = "pending"
    ui          = "pending"
    gitSha      = "unknown"
    version     = "unknown"
    resultDir   = $resultDir
}

function Write-Stage([string]$text) {
    Write-Host "== $text" -ForegroundColor Cyan
}

function Save-Result {
    $result | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $resultDir "result.json") -Encoding utf8
}

function Fail-With([int]$code, [string]$message, [string]$key) {
    if ($key) { $result[$key] = "fail: $message" }
    Save-Result
    Write-Host "FAILED: $message" -ForegroundColor Red
    Write-Host "结果目录: $resultDir" -ForegroundColor Yellow
    exit $code
}

function Get-AppProcess {
    Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
}

function Stop-AppGracefully {
    param([int]$WaitSeconds = 8)
    Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.CloseMainWindow() | Out-Null } catch { }
    }

    for ($i = 0; $i -lt $WaitSeconds; $i++) {
        Start-Sleep -Seconds 1
        if (-not (Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue)) {
            break
        }
    }

    Get-Process -Name "*BiliCopilot*" -ErrorAction SilentlyContinue | ForEach-Object {
        taskkill /F /PID $_.Id 2>&1 | Out-Null
    }
    Start-Sleep -Seconds 3
}

function Get-EventEvidence {
    param([string]$name)
    try {
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match "BiliCopilot" } |
            Select-Object -First 30 |
            ForEach-Object { "$($_.TimeCreated.ToString('O')) | $($_.ProviderName) | $($_.Id) | $(($_.Message -replace "`r?`n", ' '))" } |
            Set-Content (Join-Path $resultDir "$name.txt") -Encoding utf8
    }
    catch { }
}

function Find-OkMarker {
    foreach ($root in $traceRoots) {
        $marker = Join-Path $root "startup-ok.txt"
        if (Test-Path $marker) { return $marker }
    }
    return $null
}

function Clear-OkMarkers {
    foreach ($root in $traceRoots) {
        $marker = Join-Path $root "startup-ok.txt"
        try { if (Test-Path $marker) { Remove-Item $marker -Force } } catch { }
    }
}

function Wait-ForReady {
    param([datetime]$since, [int]$timeoutSeconds)

    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        $proc = Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue
        if (-not $proc) {
            return @{ ok = $false; reason = "process-exited" }
        }

        $marker = Find-OkMarker
        if ($marker) {
            $markerTime = (Get-Item $marker).LastWriteTime
            if ($markerTime -ge $since) {
                $win = $proc | Where-Object { $_.MainWindowHandle -ne 0 }
                if (-not $win) {
                    return @{ ok = $false; reason = "ready-marker-but-no-window" }
                }

                return @{ ok = $true; pid = $win[0].Id }
            }
        }
    }

    $any = Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue
    if ($any) {
        return @{ ok = $false; reason = "timeout-with-process-alive(=UI unresponsive)" }
    }

    return @{ ok = $false; reason = "timeout-with-process-gone" }
}

# ---------------------------------------------------------------- 0. 准备

Write-Stage "0/6 环境检查"
if (-not (Test-Path $binExe)) {
    Fail-With -code 7 -message "找不到构建产物: $binExe" -key "deploy"
}

$gitSha = "unknown"
try {
    $gitSha = (& git -C $repo rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $gitSha = "unknown" }
}
catch { }
$result.gitSha = $gitSha
$env:BILICOPILOT_GIT_SHA = $gitSha
$env:BILICOPILOT_BUILD_STAMP = $stamp
Write-Host "git sha: $gitSha"

Write-Stage "1/6 结束旧进程"
Stop-AppGracefully

# ---------------------------------------------------------------- 1. 构建

if (-not $NoBuild) {
    Write-Stage "2/6 构建 Debug x64"
    $buildLog = Join-Path $resultDir "build.log"
    Push-Location (Join-Path $repo "src")
    try {
        & D:\dotnet\dotnet.exe build "Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj" -c Debug -p:Platform=x64 "-p:SolutionDir=$repo\src\" 2>&1 |
            Tee-Object -FilePath $buildLog
        $buildExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($buildExit -ne 0) {
        $result.build = "fail: exit $buildExit"
        Fail-With -code 2 -message "构建失败，详见 $buildLog" -key "build"
    }

    $result.build = "ok"
}
else {
    $result.build = "skipped(-NoBuild)"
}

# ---------------------------------------------------------------- 2. 部署 / 确认跑的是当前产物

Write-Stage "3/6 注册并核对包指向当前构建产物"
if ($Reinstall) {
    Write-Host "（-Reinstall：会清应用数据）" -ForegroundColor Yellow
    Get-AppxPackage -Name $packageName | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
    Start-Sleep -Seconds 3
}

$manifest = Join-Path $bin "AppxManifest.xml"
try {
    Add-AppxPackage -Register $manifest -ErrorAction Stop
}
catch {
    $result.deploy = "fail: $($_.Exception.Message)"
    Save-Result
    Get-EventEvidence "events-deploy-failed"
    Fail-With -code 3 -message "注册失败: $($_.Exception.Message)"
}

$pkg = Get-AppxPackage -Name $packageName
if (-not $pkg) {
    Fail-With -code 3 -message "注册后仍找不到包" -key "deploy"
}

$installLocation = $pkg.InstallLocation
$result.version = $pkg.Version.ToString()
if ($installLocation -and ($installLocation.TrimEnd('\') -ne $bin.TrimEnd('\'))) {
    $result.deploy = "fail: 已安装包指向 $installLocation，不是当前产物 $bin"
    Fail-With -code 3 -message "跑的不是当前构建产物：$installLocation"
}

$result.deploy = "ok: $installLocation (v$($pkg.Version))"
Write-Host "安装位置: $installLocation 版本 $($pkg.Version)"

# ---------------------------------------------------------------- 3. 第一次启动

Write-Stage "4/6 第一次启动"
$launch1Start = Get-Date
Clear-OkMarkers
Start-Process "shell:AppsFolder\$aumid"
$ready1 = Wait-ForReady -since $launch1Start -timeoutSeconds $LaunchTimeoutSeconds
if (-not $ready1.ok) {
    $result.launch1 = "fail: $($ready1.reason)"
    Save-Result
    Get-EventEvidence "events-launch1-failed"
    Fail-With -code 4 -message "第一次启动失败：$($ready1.reason)"
}

$result.launch1 = "ok: pid=$($ready1.pid)"
Write-Host "第一次启动成功 PID=$($ready1.pid)" -ForegroundColor Green

# ---------------------------------------------------------------- 4. UIA 检查

Write-Stage "5/6 UIA 检查（主窗口 / 导航 / 基础交互）"
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $proc = Get-AppProcess | Select-Object -First 1
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    if (-not $root) {
        throw "取不到主窗口自动化根"
    }

    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $names = @()
    for ($i = 0; $i -lt $all.Count; $i++) {
        $n = $all[$i].Current.Name
        if ($n) { $names += $n }
    }

    $navExpected = @("搜索", "动态", "直播", "设置")
    $navFound = @()
    foreach ($item in $navExpected) {
        if ($names -contains $item) { $navFound += $item }
    }

    $names | Sort-Object -Unique | Set-Content (Join-Path $resultDir "uia-names.txt") -Encoding utf8

    if ($navFound.Count -lt 3) {
        throw "导航项不足：找到 [$($navFound -join ',')]，期望包含 $($navExpected -join '/')"
    }

    # 基础交互：点一次"动态"，看窗口是否还活着。ListItem 不一定支持 Invoke，
    # 依次尝试 Invoke / SelectionItem，都不支持就跳过（不把"控件不支持模式"判成失败）
    $navElement = $all | Where-Object { $_.Current.Name -eq "动态" } | Select-Object -First 1
    $interaction = "skipped(no-pattern)"
    if ($navElement) {
        $invoked = $false
        try {
            $invoke = $navElement.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
            $invoked = $true
            $interaction = "invoke"
        }
        catch { }

        if (-not $invoked) {
            try {
                $select = $navElement.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $select.Select()
                $invoked = $true
                $interaction = "select"
            }
            catch { }
        }
    }

    Start-Sleep -Seconds 3
    if (-not (Get-AppProcess)) {
        throw "点击导航后窗口消失"
    }

    $result.ui = "ok: nav=$($navFound -join '|') interaction=$interaction"
    Write-Host "UIA 检查通过：$($navFound -join ' / ')" -ForegroundColor Green
}
catch {
    $result.ui = "fail: $($_.Exception.Message)"
    Save-Result
    Get-EventEvidence "events-uia-failed"
    Fail-With -code 6 -message "UIA 检查失败：$($_.Exception.Message)"
}

# ---------------------------------------------------------------- 5. 关闭再启动（重点回归）

Write-Stage "6/6 关闭并第二次启动（验证'重开打不开'）"
Stop-AppGracefully
$launch2Start = Get-Date
Clear-OkMarkers
Start-Process "shell:AppsFolder\$aumid"
$ready2 = Wait-ForReady -since $launch2Start -timeoutSeconds $LaunchTimeoutSeconds
if (-not $ready2.ok) {
    $result.launch2 = "fail: $($ready2.reason)"
    Save-Result
    Get-EventEvidence "events-launch2-failed"
    Fail-With -code 5 -message "第二次启动失败：$($ready2.reason)"
}

$result.launch2 = "ok: pid=$($ready2.pid)"
Write-Host "第二次启动成功 PID=$($ready2.pid)" -ForegroundColor Green

# 收尾：正常关闭，留下日志
Stop-AppGracefully
foreach ($root in $traceRoots) { Copy-Item (Join-Path $root "startup-*.log") $resultDir -ErrorAction SilentlyContinue }
Get-EventEvidence "events-final"
Save-Result

Write-Host ""
Write-Host "全部通过：build / deploy / launch1 / launch2 / ui" -ForegroundColor Green
Write-Host "结果目录: $resultDir" -ForegroundColor Green
Write-Host "启动日志: $($traceRoots -join ' | ')" -ForegroundColor Green
exit 0




