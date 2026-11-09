# 播放器生命周期压测
#
# 循环：冷启动 → 搜索 → 打开视频 → 等播放 → 关播放窗口 → 等退出，重复 N 轮；
# 然后用 player-lifecycle 日志统计 create / dispose 是否配对，检查泄漏。
#
# 用法：
#   pwsh -File tools\stress-player.ps1            # 默认 10 轮
#   pwsh -File tools\stress-player.ps1 -Rounds 3  # 快速验证脚本本身

param(
    [int]$Rounds = 10,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $repo "src\_build\x64\Debug\BiliCopilot.UI\bin"
$manifest = Join-Path $bin "AppxManifest.xml"
$okMarker = Join-Path $bin "startup-trace\startup-ok.txt"
$aumid = "shell:AppsFolder\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App"
$packageName = "Richasy.BiliCopilot.Dev"
$keywords = @("原神", "崩坏", "塞尔达", "怪物猎人", "极限竞速", "黑神话", "星穹铁道", "我的世界")

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$resultDir = Join-Path $repo "tools\stress-results\$stamp"
New-Item -ItemType Directory -Path $resultDir -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class StressNative
{
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static readonly IntPtr TopMost = new IntPtr(-1);
    public static readonly IntPtr NoTopMost = new IntPtr(-2);
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(180);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(100);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
"@

function Get-AppWindow {
    Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
}

function Get-PlayerWindow {
    Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -and $_.MainWindowTitle -ne "哔哩助理" }
}

function Stop-App {
    Get-Process -Name "*BiliCopilot*" -ErrorAction SilentlyContinue | ForEach-Object {
        taskkill /F /PID $_.Id 2>&1 | Out-Null
    }
    Start-Sleep -Seconds 3
}

function Start-AppFresh {
    Remove-Item $okMarker -Force -ErrorAction SilentlyContinue
    Start-Process $aumid
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 800
        if ((Test-Path $okMarker) -and (Get-AppWindow)) {
            return $true
        }
        if (-not (Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue)) {
            return $false
        }
    }
    return $false
}

function Invoke-Search([string]$keyword) {
    $proc = Get-AppWindow | Select-Object -First 1
    if (-not $proc) { return $false }

    $h = $proc.MainWindowHandle
    [StressNative]::SetWindowPos($h, [StressNative]::TopMost, 40, 20, 1500, 950, 0x0040) | Out-Null
    [StressNative]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 800

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $edit = $null
    for ($i = 0; $i -lt $all.Count; $i++) {
        if ($all[$i].Current.ControlType.ProgrammaticName -eq "ControlType.Edit") { $edit = $all[$i]; break }
    }

    if (-not $edit) { return $false }

    $edit.SetFocus()
    Start-Sleep -Milliseconds 300
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($keyword)
    Start-Sleep -Milliseconds 400
    $edit.SetFocus()
    [StressNative]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero)
    [StressNative]::keybd_event(0x0D, 2, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 7
    return $true
}

function Open-FirstResult {
    $proc = Get-AppWindow | Select-Object -First 1
    if (-not $proc) { return $false }

    $h = $proc.MainWindowHandle
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $rect = $null
    for ($i = 0; $i -lt $all.Count; $i++) {
        $e = $all[$i]
        $r = $e.Current.BoundingRectangle
        if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.Text" -and $r.Y -gt 300 -and $r.Y -lt 800 -and $e.Current.Name.Length -gt 8) {
            $rect = $r
            break
        }
    }

    if (-not $rect) { return $false }

    [StressNative]::Click([int]($rect.X + 60), [int]($rect.Y - 70))
    Start-Sleep -Seconds 12
    return [bool](Get-PlayerWindow)
}

function Close-PlayerWindow {
    $pw = Get-PlayerWindow | Select-Object -First 1
    if (-not $pw) { return $false }

    [StressNative]::PostMessage($pw.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 5
    return $true
}

function Get-LifecycleLog {
    $pf = (Get-AppxPackage -Name $packageName).PackageFamilyName
    $logger = Join-Path $env:LOCALAPPDATA "Packages\$pf\LocalState\Logger"
    Get-ChildItem $logger -Filter "player-lifecycle-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not $NoBuild) {
    Write-Host "== 构建"
    Push-Location (Join-Path $repo "src")
    try {
        & D:\dotnet\dotnet.exe build "Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj" -c Debug -p:Platform=x64 "-p:SolutionDir=$repo\src\" 2>&1 |
            Tee-Object -FilePath (Join-Path $resultDir "build.log") | Select-Object -Last 3
    }
    finally {
        Pop-Location
    }
}

# 版本号必须递增，否则注册会被拒（0x80073D06）
$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($installed) {
    $text = Get-Content $manifest -Raw
    if ($text -match 'Version="(\d+)\.(\d+)\.(\d+)\.(\d+)"') {
        $parts = $installed.Version.ToString().Split(".")
        if ($parts.Count -eq 3) { $parts += "0" }
        $parts[3] = ([int]$parts[3] + 1).ToString()
        $text = [regex]::Replace($text, 'Version="\d+\.\d+\.\d+\.\d+"', ('Version="{0}"' -f ($parts -join ".")), 1)
        Set-Content -Path $manifest -Value $text -Encoding utf8
    }
}

Stop-App
Add-AppxPackage -Register $manifest -ErrorAction SilentlyContinue

$logFile = (Get-LifecycleLog).FullName
if (-not $logFile) {
    Write-Host "找不到 player-lifecycle 日志，先手动跑一次应用" -ForegroundColor Red
    exit 7
}

$startLine = (Get-Content $logFile).Count
$roundResults = @()

for ($i = 1; $i -le $Rounds; $i++) {
    $keyword = $keywords[($i - 1) % $keywords.Count]
    Write-Host "== 第 $i/$Rounds 轮（关键词：$keyword）" -ForegroundColor Cyan

    if (-not (Start-AppFresh)) {
        $roundResults += [pscustomobject]@{ Round = $i; Keyword = $keyword; Result = "启动失败" }
        Stop-App
        continue
    }

    if (-not (Invoke-Search $keyword)) {
        $roundResults += [pscustomobject]@{ Round = $i; Keyword = $keyword; Result = "搜索失败" }
        Stop-App
        continue
    }

    if (-not (Open-FirstResult)) {
        $roundResults += [pscustomobject]@{ Round = $i; Keyword = $keyword; Result = "未打开播放窗口" }
        Stop-App
        continue
    }

    $closed = Close-PlayerWindow
    Stop-App
    $roundResults += [pscustomobject]@{ Round = $i; Keyword = $keyword; Result = $(if ($closed) { "播放+关闭 OK" } else { "关闭失败" }) }
}

# 统计 create / dispose 配对
$lines = Get-Content $logFile | Select-Object -Skip $startLine
$creates = ($lines | Where-Object { $_ -match "create-player:start" }).Count
$disposes = ($lines | Where-Object { $_ -match "dispose:start" }).Count
$skipped = ($lines | Where-Object { $_ -match "skipped \(already disposed\)" }).Count
$elapsedCreate = $lines | Where-Object { $_ -match "create-player:end" } | ForEach-Object { if ($_ -match "elapsed=(\d+)ms") { [int]$Matches[1] } }

$summary = [ordered]@{
    rounds          = $Rounds
    createCount     = $creates
    disposeCount    = $disposes
    skippedDispose  = $skipped
    leaked          = $creates - $disposes
    createMsAvg     = if ($elapsedCreate) { [math]::Round(($elapsedCreate | Measure-Object -Average).Average) } else { 0 }
    createMsMax     = if ($elapsedCreate) { ($elapsedCreate | Measure-Object -Maximum).Maximum } else { 0 }
    resultDir       = $resultDir
}

$roundResults | Format-Table -AutoSize | Out-String | Write-Host
$summary | ConvertTo-Json | Set-Content (Join-Path $resultDir "summary.json") -Encoding utf8
$lines | Set-Content (Join-Path $resultDir "lifecycle-slice.log") -Encoding utf8
$summary | ConvertTo-Json | Write-Host

if ($summary.leaked -ne 0) {
    Write-Host "FAILED：create=$creates 与 dispose=$disposes 不配对，疑似泄漏" -ForegroundColor Red
    exit 5
}

Write-Host "全部通过：create 与 dispose 配对，无泄漏" -ForegroundColor Green
exit 0

