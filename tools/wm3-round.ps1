# PERFWM3 round runner: cold start -> open one video by UIA (real control) -> wait -> harvest log slice.
# ASCII only on purpose (console encoding).
param(
    [int]$Round = 1,
    [int]$WaitHome = 30,
    [int]$WaitAfterClick = 40,
    [switch]$DumpOnly,
    [int]$Card = 0
)

$ErrorActionPreference = "Continue"
$pkgName = "Richasy.BiliCopilot.Dev"
$aumid = "shell:AppsFolder\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App"
$logDir = Join-Path $env:LOCALAPPDATA ("Packages\{0}\LocalState\Logger" -f (Get-AppxPackage -Name $pkgName).PackageFamilyName)
$logFile = Join-Path $logDir ("log-{0}.txt" -f (Get-Date -Format "yyyy-MM-dd"))
$lifeFile = Join-Path $logDir ("player-lifecycle-{0}.log" -f (Get-Date -Format "yyyyMMdd"))

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class W3Win
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static List<string[]> List(uint target)
    {
        var res = new List<string[]>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var t = new StringBuilder(512); GetWindowText(h, t, 512);
                res.Add(new string[] { h.ToString(), t.ToString() });
            }
            return true;
        }, IntPtr.Zero);
        return res;
    }
}
"@

function Stop-App {
    Get-Process -Name "*BiliCopilot*" -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }
    Start-Sleep -Seconds 4
}

function Get-AppProc {
    Get-Process -Name "BiliCopilot.UI" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}

function Get-PlayerWindows([int]$procId) {
    [W3Win]::List([uint32]$procId) | Where-Object { $_.GetValue(1) -and $_.GetValue(1) -ne "哔哩助理" }
}

function Read-Slice([string]$path, [long]$offset) {
    if (-not (Test-Path $path)) { return "" }
    $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $len = $fs.Length
        $n = $len - $offset
        if ($n -le 0) { return "" }
        $fs.Seek($offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $buf = New-Object byte[] $n
        $read = $fs.Read($buf, 0, $n)
        return [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
    }
    finally { $fs.Close() }
}

Write-Host "== ROUND $Round : stop app =="
Stop-App

$offset = 0
if (Test-Path $logFile) { $offset = (Get-Item $logFile).Length }
$lifeOffset = 0
if (Test-Path $lifeFile) { $lifeOffset = (Get-Item $lifeFile).Length }
Write-Host "log=$logFile offset=$offset round=$Round"

Write-Host "== start app =="
$launchAt = Get-Date
Start-Process $aumid

$deadline = (Get-Date).AddSeconds(45)
$proc = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    $proc = Get-AppProc
    if ($proc) { break }
}
if (-not $proc) { Write-Host "FAIL: no main window"; exit 2 }
Write-Host ("main window after {0:N1}s pid={1}" -f ((Get-Date) - $launchAt).TotalSeconds, $proc.Id)

$elapsed = ((Get-Date) - $launchAt).TotalSeconds
if ($elapsed -lt $WaitHome) { Start-Sleep -Seconds ([int]($WaitHome - $elapsed)) }

$extra = Get-PlayerWindows $proc.Id
if ($extra) {
    Write-Host "WARN: a player window already exists before click (auto-opened): $($extra.Count)"
    foreach ($w in $extra) { Write-Host ("   close hwnd={0} title=[{1}]" -f $w[0], $w[1]); [W3Win]::PostMessage([IntPtr][long]$w[0], 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    Start-Sleep -Seconds 8
    $proc = Get-AppProc
}

$h = $proc.MainWindowHandle
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Host "uia elements: $($all.Count)"

$cards = @()
$navItems = @()
for ($i = 0; $i -lt $all.Count; $i++) {
    $e = $all[$i]
    try {
        $ct = $e.Current.ControlType.ProgrammaticName
        $autoId = $e.Current.AutomationId
        $r = $e.Current.BoundingRectangle
        if ($ct -eq "ControlType.Button" -and $autoId -eq "RootCard" -and (-not $e.Current.IsOffscreen) -and $r.Width -gt 300) {
            $cards += [pscustomobject]@{ Idx = $i; X = [int]$r.X; Y = [int]$r.Y; W = [int]$r.Width; H = [int]$r.Height; El = $e; Title = "" }
        }
        elseif ($ct -eq "ControlType.ListItem" -and $e.Current.Name) {
            $navItems += [pscustomobject]@{ Idx = $i; Name = $e.Current.Name; El = $e }
        }
    }
    catch { continue }
}

Write-Host "--- nav items ---"
$navItems | Select-Object -First 20 | ForEach-Object { Write-Host ("  [{0}] {1}" -f $_.Idx, $_.Name) }

# attach a plausible title (longest text inside the card) for the report
foreach ($c in $cards) {
    try {
        $texts = $c.El.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $best = ""
        for ($j = 0; $j -lt $texts.Count; $j++) {
            $t = $texts[$j].Current
            if ($t.ControlType.ProgrammaticName -eq "ControlType.Text" -and $t.Name -and $t.Name.Length -gt $best.Length -and $t.Name.Length -lt 120) { $best = $t.Name }
        }
        $c.Title = $best
    }
    catch { }
}

Write-Host "--- video cards ---"
$cards | ForEach-Object { Write-Host ("  [{0}] title=[{1}] @ {2},{3} {4}x{5}" -f $_.Idx, $_.Title, $_.X, $_.Y, $_.W, $_.H) }

if ($DumpOnly) { exit 0 }

if ($cards.Count -eq 0) { Write-Host "FAIL: no video card button found"; exit 3 }

$idx = [Math]::Min($Card, $cards.Count - 1)
$target = $cards[$idx]
Write-Host ("PICK: card[{0}] title=[{1}] rect={2},{3} {4}x{5}" -f $target.Idx, $target.Title, $target.X, $target.Y, $target.W, $target.H)
try {
    $pat = $target.El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $clickAt = Get-Date
    $pat.Invoke()
    Write-Host ("Invoke OK at {0:HH:mm:ss.fff}" -f $clickAt)
}
catch {
    Write-Host ("Invoke failed: {0}" -f $_.Exception.Message)
    exit 4
}

$opened = $false
for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep -Milliseconds 500
    if (Get-PlayerWindows $proc.Id) { $opened = $true; break }
}
Write-Host ("player window after click={0} (+{1:N1}s)" -f $opened, ((Get-Date) - $clickAt).TotalSeconds)

$elapsed = ((Get-Date) - $clickAt).TotalSeconds
if ($elapsed -lt $WaitAfterClick) { Start-Sleep -Seconds ([int]($WaitAfterClick - $elapsed)) }

Write-Host ("after wait: app alive={0}" -f [bool](Get-Process -Id $proc.Id -ErrorAction SilentlyContinue))
# player window check via UIA desktop children of this pid
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
Write-Host "windows after wait=$($wins.Count)"
for ($i = 0; $i -lt $wins.Count; $i++) {
    Write-Host ("  win[{0}] name=[{1}] class=[{2}] rect={3}" -f $i, $wins[$i].Current.Name, $wins[$i].Current.ClassName, $wins[$i].Current.BoundingRectangle)
}

Write-Host "=== LOG SLICE (probe lines) ==="
$slice = Read-Slice $logFile $offset
($slice -split "`r?`n") | Where-Object { $_ -match "PERFWM3:|PERF UiLag|PERF Load\.|PERF COUNT Load|播放器初始化超过|初始化：连接器就绪|初始化：播放器就绪" } | ForEach-Object { Write-Host $_ }

Write-Host "=== LIFECYCLE SLICE ==="
$lifeSlice = Read-Slice $lifeFile $lifeOffset
($lifeSlice -split "`r?`n") | Where-Object { $_ -match "create-player|create-mpv-client|dispose:start" } | ForEach-Object { Write-Host $_ }

Write-Host "== ROUND $Round done =="
