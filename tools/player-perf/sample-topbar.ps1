#Requires -Version 7.0
<#
.SYNOPSIS
    Samples the Bili.Copilot dev build's top-bar show/hide performance with mouse clicks,
    then parses the app's PERF log into a Markdown summary.

.DESCRIPTION
    Steps:
      1. create LocalState\perf-probe.txt (the app only records PERF while it exists),
         stop any running BiliCopilot.UI, launch the app, wait for its main window (<= 60 s)
      2. UI Automation: nav "搜索" -> search box "搜索视频、番剧或UP主" -> -Bvid -> ENTER,
         click a result card, wait for the player window (<= 30 s, > 1200 px wide)
      3. GetWindowRect on the player window, then per cycle:
             reveal the top bar by hovering into the title-bar band
             click the video area (plain down + up), wait -GapMs
             click the same point again, wait -GapMs
      4. sleep 12 s, force one extra visibility change (the app flushes PERF inside the
         IsControlsVisible setter, not from a timer), read LocalState\Logger\log-<date>.txt
         and keep every PERF line whose timestamp is >= script start
      5. print the probes as a Markdown table (last n / p50 / p95 / max per probe), the
         PERF COUNT totals and the raw lines; write the same rows to -OutputCsv when given

    Files touched: LocalState\perf-probe.txt (created at start, deleted in finally) and the
    optional -OutputCsv file. Nothing else is written or deleted by this script.

.PARAMETER Cycles
    Number of click pairs. Default 100.

.PARAMETER GapMs
    Pause after every click, in milliseconds. Default 350. Keep it above 250 ms: two clicks
    inside the app's 200 ms double-tap window toggle fullscreen instead of the overlay.

.PARAMETER Bvid
    Video used for the sample. Default BV13DBHBGEbs.

.PARAMETER SkipLaunch
    Attach to an already running BiliCopilot.UI instead of restarting it. The probe switch is
    cached per process (PerfProbe.IsEnabled is Lazy<bool>), so an instance that started before
    perf-probe.txt existed stays silent; restart without -SkipLaunch in that case.

.PARAMETER OutputCsv
    Optional path; one row per probe (Kind,Probe,N,P50Ms,P95Ms,MaxMs,Count,LastSeen).

.EXAMPLE
    pwsh -NoProfile -File .\sample-topbar.ps1 -Cycles 100

.EXAMPLE
    pwsh -NoProfile -File .\sample-topbar.ps1 -Cycles 2 -SkipLaunch -OutputCsv .\topbar.csv
#>
[CmdletBinding()]
param(
    [int]$Cycles = 100,
    [int]$GapMs = 350,
    [string]$Bvid = 'BV13DBHBGEbs',
    [switch]$SkipLaunch,
    [string]$OutputCsv
)

$ErrorActionPreference = 'Stop'

$appProcessName = 'BiliCopilot.UI'
$appPackageName = 'Richasy.BiliCopilot.Dev'
$scriptStart = [datetimeoffset]::Now
$script:failures = [System.Collections.Generic.List[string]]::new()
$script:probePath = $null
$script:pfn = $null
$script:playerInfo = $null
$script:preExistingHandles = @()
$script:tapX = 0
$script:tapY = 0
$script:revealX = 0
$script:revealY = 0
$script:cyclesRan = 0
$script:resultCode = 0
$script:foregroundWarned = $false
$script:foregroundOk = $false

# ---------------------------------------------------------------------------
# Win32 interop (Add-Type, as required: mouse_event / GetWindowRect /
# SetForegroundWindow / SetCursorPos plus window enumeration helpers).
# ---------------------------------------------------------------------------
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class BiliPerfWin32
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    /// <summary>left, top, width, height of the window (screen pixels), or null.</summary>
    public static int[] GetWindowRectValues(IntPtr hWnd)
    {
        RECT r;
        if (!GetWindowRect(hWnd, out r)) { return null; }
        return new[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    /// <summary>width, height of the client area, or null.</summary>
    public static int[] GetClientSizeValues(IntPtr hWnd)
    {
        RECT r;
        if (!GetClientRect(hWnd, out r)) { return null; }
        return new[] { r.Right - r.Left, r.Bottom - r.Top };
    }

    /// <summary>client point -> screen point, or null.</summary>
    public static int[] ClientToScreenValues(IntPtr hWnd, int x, int y)
    {
        POINT p;
        p.X = x;
        p.Y = y;
        if (!ClientToScreen(hWnd, ref p)) { return null; }
        return new[] { p.X, p.Y };
    }

    /// <summary>True when the window owns a child HWND with this class name (e.g. "mpv").</summary>
    public static bool HasChildClass(IntPtr hWnd, string className)
    {
        bool found = false;
        EnumChildWindows(hWnd, (child, lParam) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(child, sb, 256);
            if (string.Equals(sb.ToString(), className, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
function Add-Failure {
    param([string]$Step, [string]$Message)
    $text = "STEP $Step FAILED: $Message"
    $script:failures.Add($text) | Out-Null
    Write-Output $text
}

function Get-ProcessSafe {
    return @(Get-Process -Name $appProcessName -ErrorAction SilentlyContinue)
}

function Get-AppWindowList {
    param([int]$TargetProcessId)

    $script:enumWindows = [System.Collections.Generic.List[object]]::new()
    $script:enumTargetId = $TargetProcessId

    $callback = [BiliPerfWin32+EnumWindowsProc]{
        param([IntPtr]$hwnd, [IntPtr]$lparam)

        $procId = [uint32]0
        [BiliPerfWin32]::GetWindowThreadProcessId($hwnd, [ref]$procId) | Out-Null
        if ($procId -ne [uint32]$script:enumTargetId) { return $true }

        $rect = [BiliPerfWin32]::GetWindowRectValues($hwnd)
        $sb = [System.Text.StringBuilder]::new(256)
        [BiliPerfWin32]::GetWindowText($hwnd, $sb, 256) | Out-Null
        $classSb = [System.Text.StringBuilder]::new(256)
        [BiliPerfWin32]::GetClassName($hwnd, $classSb, 256) | Out-Null

        $left = 0; $top = 0; $width = 0; $height = 0
        if ($null -ne $rect) {
            $left = $rect[0]; $top = $rect[1]; $width = $rect[2]; $height = $rect[3]
        }

        $script:enumWindows.Add([pscustomobject]@{
            Handle    = [int64]$hwnd
            Title     = $sb.ToString()
            ClassName = $classSb.ToString()
            Visible   = [BiliPerfWin32]::IsWindowVisible($hwnd)
            Left      = $left
            Top       = $top
            Width     = $width
            Height    = $height
        })
        return $true
    }

    [BiliPerfWin32]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $script:enumWindows.ToArray()
}

function Set-AppForeground {
    param([IntPtr]$Hwnd)

    # Physical clicks go to the window under the cursor, so the target has to be
    # really in front. SetForegroundWindow alone often loses against the
    # foreground lock, so retry with BringWindowToTop and verify the result.
    $procId = [uint32]0
    $targetThread = [BiliPerfWin32]::GetWindowThreadProcessId($Hwnd, [ref]$procId)
    $currentThread = [BiliPerfWin32]::GetCurrentThreadId()

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            [BiliPerfWin32]::AttachThreadInput($currentThread, $targetThread, $true) | Out-Null
            [BiliPerfWin32]::SetForegroundWindow($Hwnd) | Out-Null
            [BiliPerfWin32]::BringWindowToTop($Hwnd) | Out-Null
            # HWND_TOP = 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW
            [BiliPerfWin32]::SetWindowPos($Hwnd, [IntPtr]::Zero, 0, 0, 0, 0, 0x0002 -bor 0x0001 -bor 0x0040) | Out-Null
        } finally {
            try {
                [BiliPerfWin32]::AttachThreadInput($currentThread, $targetThread, $false) | Out-Null
            } catch { }
        }

        Start-Sleep -Milliseconds 200
        if ([BiliPerfWin32]::GetForegroundWindow() -eq $Hwnd) {
            $script:foregroundOk = $true
            return
        }
    }

    $script:foregroundOk = ([BiliPerfWin32]::GetForegroundWindow() -eq $Hwnd)
}

function Invoke-LeftClick {
    param([int]$X, [int]$Y)

    [BiliPerfWin32]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 60
    [BiliPerfWin32]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [BiliPerfWin32]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
}

function Set-CursorInsideWindow {
    param([IntPtr]$Hwnd, [int]$X, [int]$Y)

    $rect = [BiliPerfWin32]::GetWindowRectValues($Hwnd)
    if ($null -eq $rect) {
        [BiliPerfWin32]::SetCursorPos($X, $Y) | Out-Null
        return
    }
    $left = $rect[0] + 4
    $top = $rect[1] + 4
    $right = $rect[0] + $rect[2] - 4
    $bottom = $rect[1] + $rect[3] - 4
    $cx = [Math]::Min([Math]::Max($X, $left), $right)
    $cy = [Math]::Min([Math]::Max($Y, $top), $bottom)
    [BiliPerfWin32]::SetCursorPos($cx, $cy) | Out-Null
}

function Test-MpvHost {
    param([Int64]$Handle)

    # The player window hosts an mpv child HWND; that is more reliable than UIA,
    # which sometimes returns "Unrecognized error" for a freshly created window.
    try {
        if ([BiliPerfWin32]::HasChildClass([IntPtr]$Handle, 'mpv')) { return $true }
    } catch { }

    try {
        $element = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
        if ($null -eq $element) { return $false }
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'mpv')
        $hit = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        return ($null -ne $hit)
    } catch {
        return $false
    }
}

function Wait-AppWindow {
    param([int]$TimeoutSec = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $processes = Get-ProcessSafe
        if ($processes.Count -gt 0) {
            $proc = $processes | Sort-Object StartTime -Descending | Select-Object -First 1
            $windows = Get-AppWindowList -TargetProcessId $proc.Id
            $visible = @($windows | Where-Object { $_.Visible -and $_.Width -gt 600 -and $_.Height -gt 400 })

            # The main XAML window uses WinUIDesktopWin32WindowClass; the player is
            # an AppWindow (Microsoft.UI.Windowing.Window). When the player is open
            # the main window is hidden, so the player may be the only visible one.
            $appWindowClass = 'Microsoft.UI.Windowing.Window'
            $player = @($visible | Where-Object { $_.ClassName -eq $appWindowClass }) |
                Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1

            $main = @($visible | Where-Object { $_.ClassName -eq 'WinUIDesktopWin32WindowClass' }) |
                Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
            if ($null -eq $main) {
                # Last resort: the process' MainWindowHandle, but never the player window.
                $mainWindowHandle = [int64]$proc.MainWindowHandle
                $main = @($visible | Where-Object {
                        $mainWindowHandle -ne 0 -and $_.Handle -eq $mainWindowHandle -and
                        ($null -eq $player -or $_.Handle -ne $player.Handle)
                    }) | Select-Object -First 1
            }

            if ($null -ne $main -or $null -ne $player) {
                return [pscustomobject]@{
                    Process = $proc
                    Window  = $main
                    Player  = $player
                    Windows = $windows
                }
            }
        }
        Start-Sleep -Milliseconds 700
    }
    return $null
}

function Find-ElementByName {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Name,
        [int]$TimeoutSec = 0
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        try {
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
            $element = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $element) { return $element }
        } catch { }
        if ($TimeoutSec -gt 0) { Start-Sleep -Milliseconds 500 }
    } while ($TimeoutSec -gt 0 -and (Get-Date) -lt $deadline)

    return $null
}

function Find-NavItem {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Name
    )

    try {
        $windowRect = $Window.Current.BoundingRectangle
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $items = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        foreach ($item in $items) {
            try {
                if ($item.Current.Name -ne $Name) { continue }
                $rect = $item.Current.BoundingRectangle
                if ([double]::IsNaN($rect.Left) -or [double]::IsInfinity($rect.Left)) { continue }
                if (($rect.Left - $windowRect.Left) -lt 280) { return $item }
            } catch { }
        }
    } catch { }

    # Fallback: any descendant with that name (ItemContainer patterns vary per theme).
    return Find-ElementByName -Window $Window -Name $Name
}

function Find-SearchEdit {
    param([System.Windows.Automation.AutomationElement]$Window)

    $edit = Find-ElementByName -Window $Window -Name '搜索视频、番剧或UP主' -TimeoutSec 15
    if ($null -ne $edit) { return $edit }

    # Fallback: first edit control near the top of the window.
    try {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)
        $edits = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        $windowRect = $Window.Current.BoundingRectangle
        foreach ($candidate in $edits) {
            try {
                $rect = $candidate.Current.BoundingRectangle
                if ($rect.Width -lt 150) { continue }
                if (($rect.Top - $windowRect.Top) -lt 400) { return $candidate }
            } catch { }
        }
        if ($edits.Count -gt 0) { return $edits[0] }
    } catch { }

    return $null
}

function Wait-PlayerWindow {
    param(
        [Int64]$MainHandle,
        [int64[]]$ExistingHandles,
        [int]$TimeoutSec = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $seen = @()
    while ((Get-Date) -lt $deadline) {
        $processes = Get-ProcessSafe
        if ($processes.Count -eq 0) {
            Write-Output '- app process exited while waiting for the player window'
            return $null
        }
        $proc = $processes | Select-Object -First 1
        $windows = Get-AppWindowList -TargetProcessId $proc.Id
        $candidates = @($windows | Where-Object {
                $_.Visible -and $_.Handle -ne $MainHandle -and $_.Width -gt 400 -and $_.Height -gt 250
            })
        if ($candidates.Count -gt 0) { $seen = $candidates }

        # 1) definitively the player: a window that hosts the mpv child
        foreach ($candidate in $candidates) {
            if (Test-MpvHost -Handle $candidate.Handle) {
                Write-Host ("- player window found via mpv child: hwnd=0x{0:x} {1}x{2}" -f $candidate.Handle, $candidate.Width, $candidate.Height)
                return $candidate
            }
        }

        # 1b) the player is created via AppWindow.Create(), so its HWND class is
        #     Microsoft.UI.Windowing.Window (the main XAML window is different).
        $byClass = @($candidates | Where-Object { $_.ClassName -eq 'Microsoft.UI.Windowing.Window' })
        if ($byClass.Count -gt 0) {
            $pick = $byClass | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
            Write-Host ("- player window found via AppWindow class: hwnd=0x{0:x} {1}x{2}" -f $pick.Handle, $pick.Width, $pick.Height)
            return $pick
        }

        # 2) fallback from the task spec: a new window wider than 1200 px
        $big = @($candidates | Where-Object {
                $_.Width -gt 1200 -and ($ExistingHandles -notcontains $_.Handle)
            })
        if ($big.Count -gt 0) {
            $pick = $big | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
            Write-Host ("- player window found via size (>1200px): hwnd=0x{0:x} {1}x{2}" -f $pick.Handle, $pick.Width, $pick.Height)
            return $pick
        }

        Start-Sleep -Milliseconds 800
    }

    if ($seen.Count -gt 0) {
        Write-Host ('- windows owned by the app while waiting: ' + (($seen | ForEach-Object { "0x{0:x} {1}x{2} '{3}'" -f $_.Handle, $_.Width, $_.Height, $_.Title }) -join '; '))
    }
    return $null
}

function Get-LogPath {
    param([string]$LocalState)

    $loggerDir = Join-Path $LocalState 'Logger'
    if (-not (Test-Path -LiteralPath $loggerDir)) { return $null }

    $today = (Get-Date).ToString('yyyy-MM-dd')
    $todayLog = Join-Path $loggerDir "log-$today.txt"
    if (Test-Path -LiteralPath $todayLog) { return $todayLog }

    $newest = Get-ChildItem -LiteralPath $loggerDir -Filter 'log-*.txt' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) { return $newest.FullName }
    return $null
}

function Read-PerfLog {
    param(
        [string]$Path,
        [datetimeoffset]$Since
    )

    $perfLines = [System.Collections.Generic.List[object]]::new()
    $latency = [ordered]@{}
    $counters = [ordered]@{}
    $unparsed = [System.Collections.Generic.List[string]]::new()
    $linesAfter = 0
    $lastTs = $null

    $lines = [System.Collections.Generic.List[string]]::new()
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $reader = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8, $false)
        while ($true) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            $lines.Add($line)
        }
        $reader.Dispose()
    } finally {
        $fs.Dispose()
    }

    $lineRegex = [regex]::new(
        '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+(?<off>[+-]\d{2}:\d{2})\s+\[\w+\]\s+(?<msg>.*)$')
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture

    foreach ($line in $lines) {
        $match = $lineRegex.Match($line)
        if (-not $match.Success) { continue }

        $ts = [datetimeoffset]::MinValue
        try {
            $ts = [datetimeoffset]::ParseExact(
                "$($match.Groups['ts'].Value) $($match.Groups['off'].Value)",
                'yyyy-MM-dd HH:mm:ss.fff zzz',
                $invariant)
        } catch {
            continue
        }
        if ($ts -lt $Since) { continue }

        $linesAfter++
        $lastTs = $ts
        $message = $match.Groups['msg'].Value.Trim()
        if (-not $message.StartsWith('PERF', [System.StringComparison]::Ordinal)) { continue }

        if ($message -match '^PERF COUNT (?<name>.+?)=(?<value>\d+)$') {
            $name = $Matches['name'].Trim()
            $value = [int]$Matches['value']
            if (-not $counters.Contains($name)) {
                $counters[$name] = [pscustomobject]@{ Name = $name; Total = 0; LastTs = $ts }
            }
            $counters[$name].Total += $value
            $counters[$name].LastTs = $ts
            $perfLines.Add([pscustomobject]@{ Timestamp = $ts; Raw = $line.Trim(); Kind = 'count'; Name = $name })
        } elseif ($message -match '^PERF (?<name>.+?) buckets: (?<rest>.*?)\s*p50=(?<p50>[\d.]+)ms p95=(?<p95>[\d.]+)ms max=(?<max>[\d.]+)ms$') {
            # Histogram line, e.g. "PERF UiLag buckets: <16.7=182 <33.3=1 ... p50=...".
            $rawName = $Matches['name'].Trim()
            $rest = $Matches['rest']
            $p50 = [double]$Matches['p50']
            $p95 = [double]$Matches['p95']
            $max = [double]$Matches['max']
            $n = 0
            foreach ($bucket in [regex]::Matches($rest, '=(\d+)')) {
                $n += [int]$bucket.Groups[1].Value
            }
            $name = "$rawName buckets"
            if ($latency.Contains($name)) {
                $entry = $latency[$name]
                $entry.N = $n
                $entry.P50 = $p50
                $entry.P95 = $p95
                $entry.Max = $max
                $entry.Batches += 1
                $entry.TotalN += $n
                $entry.WorstMax = [Math]::Max($entry.WorstMax, $max)
                $entry.LastTs = $ts
            } else {
                $latency[$name] = [pscustomobject]@{
                    Name = $name; N = $n; P50 = $p50; P95 = $p95; Max = $max
                    Batches = 1; TotalN = $n; WorstMax = $max; LastTs = $ts
                }
            }
            $perfLines.Add([pscustomobject]@{ Timestamp = $ts; Raw = $line.Trim(); Kind = 'histogram'; Name = $name })
        } elseif ($message -match '^PERF (?<name>[^:]+?): n=(?<n>\d+) p50=(?<p50>[\d.]+)ms p95=(?<p95>[\d.]+)ms max=(?<max>[\d.]+)ms$') {
            $name = $Matches['name'].Trim()
            $n = [int]$Matches['n']
            $p50 = [double]$Matches['p50']
            $p95 = [double]$Matches['p95']
            $max = [double]$Matches['max']
            if ($latency.Contains($name)) {
                $entry = $latency[$name]
                $entry.N = $n
                $entry.P50 = $p50
                $entry.P95 = $p95
                $entry.Max = $max
                $entry.Batches += 1
                $entry.TotalN += $n
                $entry.WorstMax = [Math]::Max($entry.WorstMax, $max)
                $entry.LastTs = $ts
            } else {
                $latency[$name] = [pscustomobject]@{
                    Name = $name; N = $n; P50 = $p50; P95 = $p95; Max = $max
                    Batches = 1; TotalN = $n; WorstMax = $max; LastTs = $ts
                }
            }
            $perfLines.Add([pscustomobject]@{ Timestamp = $ts; Raw = $line.Trim(); Kind = 'latency'; Name = $name })
        } else {
            # "PERF UiLag ..." etc: keep the raw line, do not guess a schema.
            $unparsed.Add($line.Trim())
            $perfLines.Add([pscustomobject]@{ Timestamp = $ts; Raw = $line.Trim(); Kind = 'other'; Name = $message })
        }
    }

    return [pscustomobject]@{
        PerfLines  = $perfLines
        Latency    = $latency
        Counters   = $counters
        Unparsed   = $unparsed
        LinesAfter = $linesAfter
        LastTs     = $lastTs
    }
}

function Format-Row {
    param($Sample)

    return ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |" -f
        $Sample.Name, $Sample.N, $Sample.P50.ToString('0.00'), $Sample.P95.ToString('0.00'),
        $Sample.Max.ToString('0.00'), $Sample.Batches, $Sample.TotalN,
        $Sample.WorstMax.ToString('0.00'), $Sample.LastTs.ToString('HH:mm:ss.fff'))
}

function Format-CsvField {
    param($Value)

    $text = [string]$Value
    if ($text -match '[",\r\n]') {
        return '"' + $text.Replace('"', '""') + '"'
    }
    return $text
}

# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------
function Write-Report {
    param(
        [string]$LogPath,
        $Data,
        [int]$AttemptedCycles
    )

    Write-Output ''
    Write-Output '## Result'
    Write-Output ''
    Write-Output "- app package family: $($script:pfn)"
    Write-Output "- log file: $LogPath"
    Write-Output "- script start: $($scriptStart.ToString('yyyy-MM-dd HH:mm:ss.fff zzz'))"

    if ($null -ne $script:playerInfo) {
        Write-Output ("- player window: hwnd=0x{0:x} rect={1},{2} {3}x{4}" -f
            [int64]$script:playerInfo.Handle, $script:playerInfo.Left, $script:playerInfo.Top,
            $script:playerInfo.Width, $script:playerInfo.Height)
    } else {
        Write-Output '- player window: not found'
    }

    Write-Output "- cycles: $AttemptedCycles (2 taps/cycle, gap ${GapMs} ms)"
    if ($Data -and $null -ne $Data) {
        Write-Output "- log lines after start: $($Data.LinesAfter)"
        Write-Output "- PERF lines after start: $($Data.PerfLines.Count)"
    }

    if ($script:failures.Count -gt 0) {
        Write-Output ''
        Write-Output '### Step failures'
        foreach ($failure in $script:failures) { Write-Output "- $failure" }
    }

    if ($null -eq $Data -or $Data.PerfLines.Count -eq 0) {
        Write-Output ''
        Write-Output 'No PERF lines found after the script start.'
        $reasons = [System.Collections.Generic.List[string]]::new()
        if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath)) {
            $reasons.Add("log file does not exist: $LogPath")
        } else {
            $reasons.Add("the log contains $($Data.LinesAfter) line(s) stamped after $($scriptStart.ToString('HH:mm:ss')), but none of them are PERF lines")
            $reasons.Add('PerfProbe.IsEnabled is a Lazy<bool> cached per process: an app instance started before perf-probe.txt existed stays silent for its whole lifetime. Restart without -SkipLaunch.')
            $reasons.Add('PERF is only flushed inside the IsControlsVisible setter (PlayerViewModel.OnIsControlsVisibleChanged -> PerfProbe.FlushIfDue), so no visibility change means no summary.')
            if ($AttemptedCycles -le 0) {
                $reasons.Add('no click cycles were performed because an earlier step failed')
            }
        }
        foreach ($reason in $reasons) { Write-Output "- reason: $reason" }
    } else {
        Write-Output ''
        Write-Output '### Top-bar probes (last summary per probe)'
        Write-Output ''
        Write-Output '| Probe | n (last) | p50 (ms) | p95 (ms) | max (ms) | batches | total n | worst max (ms) | last seen |'
        Write-Output '|---|---:|---:|---:|---:|---:|---:|---:|---|'
        foreach ($key in $Data.Latency.Keys) {
            Write-Output (Format-Row -Sample $Data.Latency[$key])
        }
        Write-Output ''
        Write-Output '_n/p50/p95/max describe the last flush batch only. `batches`/`total n`/`worst max` cover every PERF summary written after the script start, so a 100-cycle run is not reduced to its last 10 s._'

        Write-Output ''
        Write-Output '### PERF COUNT totals'
        Write-Output ''
        if ($Data.Counters.Count -eq 0) {
            Write-Output '_(no PERF COUNT lines after start)_'
        } else {
            Write-Output '| Counter | total | last seen |'
            Write-Output '|---|---:|---|'
            foreach ($key in $Data.Counters.Keys) {
                $counter = $Data.Counters[$key]
                Write-Output "| $($counter.Name) | $($counter.Total) | $($counter.LastTs.ToString('HH:mm:ss.fff')) |"
            }
        }

        if ($Data.Unparsed.Count -gt 0) {
            Write-Output ''
            Write-Output '### Unparsed PERF lines (kept raw)'
            foreach ($line in $Data.Unparsed) { Write-Output "- $line" }
        }

        Write-Output ''
        Write-Output '### Raw PERF lines'
        Write-Output '```'
        foreach ($entry in $Data.PerfLines) {
            Write-Output $entry.Raw
        }
        Write-Output '```'
    }

    if ($OutputCsv) {
        try {
            $csvPath = [System.IO.Path]::GetFullPath($OutputCsv)
            $csvDir = [System.IO.Path]::GetDirectoryName($csvPath)
            if ($csvDir -and -not (Test-Path -LiteralPath $csvDir)) {
                New-Item -ItemType Directory -Path $csvDir -Force | Out-Null
            }
            $rows = [System.Collections.Generic.List[string]]::new()
            $rows.Add('Kind,Probe,N,P50Ms,P95Ms,MaxMs,Batches,TotalN,WorstMaxMs,Count,LastSeen')
            foreach ($key in $Data.Latency.Keys) {
                $sample = $Data.Latency[$key]
                $fields = @(
                    'Latency', $sample.Name, $sample.N,
                    $sample.P50.ToString('0.00'), $sample.P95.ToString('0.00'), $sample.Max.ToString('0.00'),
                    $sample.Batches, $sample.TotalN, $sample.WorstMax.ToString('0.00'), '',
                    $sample.LastTs.ToString('yyyy-MM-dd HH:mm:ss.fff zzz'))
                $rows.Add((($fields | ForEach-Object { Format-CsvField $_ }) -join ','))
            }
            foreach ($key in $Data.Counters.Keys) {
                $counter = $Data.Counters[$key]
                $fields = @(
                    'Counter', $counter.Name, '', '', '', '', '', '', '', $counter.Total,
                    $counter.LastTs.ToString('yyyy-MM-dd HH:mm:ss.fff zzz'))
                $rows.Add((($fields | ForEach-Object { Format-CsvField $_ }) -join ','))
            }
            [System.IO.File]::WriteAllLines($csvPath, $rows, [System.Text.UTF8Encoding]::new($false))
            Write-Output ''
            Write-Output "- CSV written: $csvPath"
        } catch {
            Write-Output "- WARNING: could not write CSV '$OutputCsv': $($_.Exception.Message)"
        }
    }
}

# ---------------------------------------------------------------------------
# Main workflow
# ---------------------------------------------------------------------------
function Invoke-Sampling {
    if ($Cycles -lt 1) {
        Add-Failure '0' "-Cycles must be 1 or greater (got $Cycles)."
        $script:resultCode = 2; return
    }
    if ($GapMs -lt 250) {
        Write-Output "- WARNING: -GapMs $GapMs is inside the app's 200 ms double-tap window; clicks may toggle fullscreen."
    }

    # ---- Step 1: probe switch + app start ---------------------------------
    Write-Output '## Step 1: probe switch, launch, wait for the main window'
    $package = Get-AppxPackage -Name $appPackageName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $package) {
        Add-Failure '1' "Appx package '$appPackageName' is not installed for the current user."
        $script:resultCode = 2; return
    }
    $script:pfn = $package.PackageFamilyName
    $localState = Join-Path $env:LOCALAPPDATA "Packages\$($script:pfn)\LocalState"
    if (-not (Test-Path -LiteralPath $localState)) {
        Add-Failure '1' "LocalState directory not found: $localState"
        $script:resultCode = 2; return
    }
    Write-Output "- package family: $($script:pfn)"
    Write-Output "- LocalState: $localState"

    $script:probePath = Join-Path $localState 'perf-probe.txt'
    [System.IO.File]::WriteAllText($script:probePath,
        "enabled by sample-topbar.ps1 at $($scriptStart.ToString('o'))`r`n")
    Write-Output "- perf probe enabled: $($script:probePath)"

    if (-not $SkipLaunch) {
        $running = Get-ProcessSafe
        if ($running.Count -gt 0) {
            Write-Output "- stopping $($running.Count) running $appProcessName process(es)"
            $running | Stop-Process -Force -ErrorAction SilentlyContinue
            $deadline = (Get-Date).AddSeconds(15)
            while ((Get-Date) -lt $deadline -and (Get-ProcessSafe).Count -gt 0) {
                Start-Sleep -Milliseconds 300
            }
            if ((Get-ProcessSafe).Count -gt 0) {
                Add-Failure '1' "could not stop the running $appProcessName process within 15 s"
                $script:resultCode = 2; return
            }
        }
        $launchTarget = "shell:AppsFolder\$($script:pfn)!App"
        Write-Output "- launching: $launchTarget"
        Start-Process $launchTarget
    } else {
        if ((Get-ProcessSafe).Count -eq 0) {
            Add-Failure '1' "-SkipLaunch was used, but no $appProcessName process is running."
            $script:resultCode = 2; return
        }
        Write-Output "- -SkipLaunch: attaching to the running $appProcessName process"
        Write-Output '  NOTE: the probe switch is cached per process, so a process started before perf-probe.txt existed emits no PERF.'
    }

    $app = Wait-AppWindow -TimeoutSec 60
    if ($null -eq $app) {
        Add-Failure '1' 'no BiliCopilot.UI window appeared within 60 s'
        $script:resultCode = 2; return
    }
    if ($null -ne $app.Window) {
        Write-Output ("- main window: hwnd=0x{0:x} {1}x{2} title='{3}'" -f
            [int64]$app.Window.Handle, $app.Window.Width, $app.Window.Height, $app.Window.Title)
    } else {
        Write-Output '- main window: not visible (the app is showing its player window)'
    }
    if ($null -ne $app.Player) {
        Write-Output ("- player window already open: hwnd=0x{0:x} {1}x{2}" -f
            [int64]$app.Player.Handle, $app.Player.Width, $app.Player.Height)
    }

    $script:preExistingHandles = @($app.Windows | ForEach-Object { [int64]$_.Handle })
    $mainHandle = if ($null -ne $app.Window) { [int64]$app.Window.Handle } else { [int64]0 }
    if ($mainHandle -ne 0) {
        Set-AppForeground -Hwnd ([IntPtr]$mainHandle)
        Start-Sleep -Milliseconds 500
    }

    # ---- Step 2: search + open the video ----------------------------------
    $exitCode = 0
    $player = $app.Player

    if ($null -ne $player) {
        # -SkipLaunch onto an app that is already showing the player: sample it.
        Write-Output ''
        Write-Output '## Step 2: skipped - the running app already shows a player window'
    } elseif ($mainHandle -eq 0) {
        Add-Failure '2' 'no main window is available to search from'
        $script:resultCode = 2; return
    } else {
        Write-Output ''
        Write-Output '## Step 2: search box -> result card -> player window'
    try {
        $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainHandle)
    } catch {
        Add-Failure '2' "could not attach UI Automation to the main window: $($_.Exception.Message)"
        $script:resultCode = 2; return
    }
    if ($null -eq $window) {
        Add-Failure '2' 'UI Automation returned no element for the main window handle.'
        $script:resultCode = 2; return
    }

    $navItem = Find-NavItem -Window $window -Name '搜索'
    if ($null -eq $navItem) {
        Add-Failure '2' "nav item '搜索' was not found through UI Automation"
        $script:resultCode = 2; return
    }
    $navRect = $navItem.Current.BoundingRectangle
    $navX = [int]($navRect.Left + $navRect.Width / 2)
    $navY = [int]($navRect.Top + $navRect.Height / 2)
    Write-Output "- clicking nav item '搜索' at $navX,$navY"
    Invoke-LeftClick $navX $navY
    Start-Sleep -Seconds 3

    Set-AppForeground -Hwnd ([IntPtr]$mainHandle)
    try {
        $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$mainHandle)
    } catch {
        Add-Failure '2' "main window UIA element disappeared after navigation: $($_.Exception.Message)"
        $script:resultCode = 2; return
    }

    $edit = Find-SearchEdit -Window $window
    if ($null -eq $edit) {
        Add-Failure '2' "search edit '搜索视频、番剧或UP主' was not found through UI Automation"
        $script:resultCode = 2; return
    }

    $editRect = $edit.Current.BoundingRectangle
    $editX = [int]($editRect.Left + $editRect.Width / 2)
    $editY = [int]($editRect.Top + $editRect.Height / 2)
    try { $edit.SetFocus() } catch { }
    Start-Sleep -Milliseconds 200

    $valuePattern = $null
    try {
        $valuePattern = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    } catch {
        $valuePattern = $null
    }

    if ($null -ne $valuePattern) {
        Write-Output "- setting search box value via ValuePattern: $Bvid"
        try {
            $valuePattern.SetValue($Bvid)
        } catch {
            Write-Output "- ValuePattern.SetValue failed ($($_.Exception.Message)); typing instead"
            Invoke-LeftClick $editX $editY
            Start-Sleep -Milliseconds 150
            [System.Windows.Forms.SendKeys]::SendWait('^a')
            [System.Windows.Forms.SendKeys]::SendWait($Bvid)
        }
    } else {
        Write-Output '- ValuePattern unavailable; typing into the search box'
        Invoke-LeftClick $editX $editY
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        [System.Windows.Forms.SendKeys]::SendWait($Bvid)
    }

    Start-Sleep -Milliseconds 300
    Set-AppForeground -Hwnd ([IntPtr]$mainHandle)
    try { $edit.SetFocus() } catch { }
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Write-Output '- submitted search, waiting 10 s for results'
    Start-Sleep -Seconds 10

    # click a result card at 25% width / 50% height of the client area
    $clientSize = [BiliPerfWin32]::GetClientSizeValues([IntPtr]$mainHandle)
    $clickedCard = $false
    if ($null -ne $clientSize) {
        $screenPoint = [BiliPerfWin32]::ClientToScreenValues(
            [IntPtr]$mainHandle,
            [int]($clientSize[0] * 0.25),
            [int]($clientSize[1] * 0.50))
        if ($null -ne $screenPoint) {
            Write-Output "- clicking result card at $($screenPoint[0]),$($screenPoint[1]) (25%/50% of client area)"
            Invoke-LeftClick $screenPoint[0] $screenPoint[1]
            $clickedCard = $true
        }
    }
    if (-not $clickedCard) {
        Write-Output '- GetClientRect/ClientToScreen failed; falling back to window-relative 25%/50%'
        $winRect = [BiliPerfWin32]::GetWindowRectValues([IntPtr]$mainHandle)
        if ($null -ne $winRect) {
            Invoke-LeftClick ([int]($winRect[0] + $winRect[2] * 0.25)) ([int]($winRect[1] + $winRect[3] * 0.50))
            $clickedCard = $true
        }
    }
    if (-not $clickedCard) {
        Add-Failure '2' 'could not compute a result-card click point'
        $script:resultCode = 2; return
    }

    Write-Output '- waiting up to 30 s for the player window'
    $player = Wait-PlayerWindow -MainHandle $mainHandle -ExistingHandles $script:preExistingHandles -TimeoutSec 30
    if ($null -eq $player) {
        # one retry: a slightly different point inside the results grid
        $clientSize = [BiliPerfWin32]::GetClientSizeValues([IntPtr]$mainHandle)
        if ($null -ne $clientSize) {
            $screenPoint = [BiliPerfWin32]::ClientToScreenValues(
                [IntPtr]$mainHandle,
                [int]($clientSize[0] * 0.32),
                [int]($clientSize[1] * 0.45))
            if ($null -ne $screenPoint) {
                Write-Output "- retrying the result card at $($screenPoint[0]),$($screenPoint[1]) (32%/45%)"
                Invoke-LeftClick $screenPoint[0] $screenPoint[1]
                $player = Wait-PlayerWindow -MainHandle $mainHandle -ExistingHandles $script:preExistingHandles -TimeoutSec 30
            }
        }
    }

    if ($null -eq $player) {
        Add-Failure '2' 'no player window (>1200 px wide, mpv child) appeared within 30 s of the card click'
        $exitCode = 2
    }
    }

    # ---- Step 3: click cycles ---------------------------------------------
    if ($null -ne $player) {
        Write-Output ''
        Write-Output '## Step 3: top-bar show/hide cycles'
        $script:playerInfo = [pscustomobject]@{
            Handle = $player.Handle
            Left   = $player.Left
            Top    = $player.Top
            Width  = $player.Width
            Height = $player.Height
        }

        Set-AppForeground -Hwnd ([IntPtr]$player.Handle)
        Start-Sleep -Milliseconds 600

        $rect = [BiliPerfWin32]::GetWindowRectValues([IntPtr]$player.Handle)
        if ($null -eq $rect) {
            if ((Get-ProcessSafe).Count -eq 0) {
                Add-Failure '3' 'the app process exited right after the player window appeared (crash?)'
                $exitCode = 2
            } else {
                # The player window can be recreated while the video initializes;
                # the handle we just found may already be gone.
                Write-Output '- the player window handle went stale, resolving it again'
                for ($attempt = 1; $attempt -le 10 -and $null -eq $rect; $attempt++) {
                    Start-Sleep -Milliseconds 800
                    $fresh = Wait-PlayerWindow -MainHandle $mainHandle -ExistingHandles @() -TimeoutSec 3
                    if ($null -ne $fresh) {
                        $player = $fresh
                        $script:playerInfo = [pscustomobject]@{
                            Handle = $player.Handle
                            Left   = $player.Left
                            Top    = $player.Top
                            Width  = $player.Width
                            Height = $player.Height
                        }
                        $rect = [BiliPerfWin32]::GetWindowRectValues([IntPtr]$player.Handle)
                    }
                }
                if ($null -eq $rect) {
                    Add-Failure '3' 'GetWindowRect failed for the player window'
                    $exitCode = 2
                }
            }
        }
        if ($null -ne $rect) {
            $script:tapX = [int]($rect[0] + $rect[2] / 2)
            $script:tapY = [int]($rect[1] + $rect[3] * 0.55)
            $script:revealX = [int]($rect[0] + $rect[2] / 2)
            $script:revealY = [int]($rect[1] + 30)
            Write-Output "- player rect: left=$($rect[0]) top=$($rect[1]) $($rect[2])x$($rect[3])"
            Write-Output "- tap point: $($script:tapX),$($script:tapY)"
            Write-Output "- reveal hover: $($script:revealX),$($script:revealY)"

            $aborted = $false
            for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
                if ((Get-ProcessSafe).Count -eq 0) {
                    Add-Failure '3' "app process exited during cycle $cycle"
                    $exitCode = 2
                    $aborted = $true
                    break
                }
                if (-not [BiliPerfWin32]::IsWindow([IntPtr]$player.Handle)) {
                    Add-Failure '3' "player window closed during cycle $cycle"
                    $exitCode = 2
                    $aborted = $true
                    break
                }

                # Physical clicks land on whatever window is under the cursor;
                # keep the player in front so the taps reach the app.
                if ([BiliPerfWin32]::GetForegroundWindow() -ne [IntPtr]$player.Handle) {
                    Set-AppForeground -Hwnd ([IntPtr]$player.Handle)
                    if (-not $script:foregroundOk -and -not $script:foregroundWarned) {
                        Write-Output '- WARNING: the player window is not the foreground window; physical clicks may land on another window'
                        $script:foregroundWarned = $true
                    }
                }

                # The window can be moved/resized between cycles (e.g. a stray
                # double click); recompute the points and stay inside the window.
                $current = [BiliPerfWin32]::GetWindowRectValues([IntPtr]$player.Handle)
                if ($null -ne $current) {
                    $script:tapX = [int]($current[0] + $current[2] / 2)
                    $script:tapY = [int]($current[1] + $current[3] * 0.55)
                    $script:revealX = [int]($current[0] + $current[2] / 2)
                    $script:revealY = [int]($current[1] + 30)
                }

                # Mouse input toggles the overlay on pointer enter/leave, not on
                # tap (a mouse tap is play/pause; touch toggles IsControlsVisible).
                # The hover below is what makes the "show" half real. Give the
                # pointer move a moment to land before the next move, otherwise
                # WinUI coalesces the two positions and the show is never seen.
                Set-CursorInsideWindow -Hwnd ([IntPtr]$player.Handle) -X $script:revealX -Y $script:revealY
                Start-Sleep -Milliseconds 300
                Set-CursorInsideWindow -Hwnd ([IntPtr]$player.Handle) -X $script:tapX -Y $script:tapY
                Start-Sleep -Milliseconds 80
                Invoke-LeftClick $script:tapX $script:tapY
                Start-Sleep -Milliseconds $GapMs
                Invoke-LeftClick $script:tapX $script:tapY
                Start-Sleep -Milliseconds $GapMs
                $script:cyclesRan = $cycle

                if ($cycle % 10 -eq 0 -and $cycle -lt $Cycles) {
                    Write-Output "  cycle $cycle/$Cycles"
                }
            }
            Write-Output "- completed $($script:cyclesRan)/$Cycles cycles"
            if ($aborted) { $exitCode = 2 }
        }
    }

    # ---- Step 4: wait for the flush, read the log -------------------------
    Write-Output ''
    Write-Output '## Step 4: flush + read the log'
    if ($script:cyclesRan -gt 0) {
        Write-Output '- sleeping 12 s for the 10 s flush window'
        Start-Sleep -Seconds 12

        # PerfProbe.FlushIfDue is only called from the IsControlsVisible setter, so an idle
        # sleep writes nothing. Give the app two chances to change visibility: any change
        # flushes the pending batch.
        if ((Get-ProcessSafe).Count -gt 0 -and $null -ne $script:playerInfo -and
            [BiliPerfWin32]::IsWindow([IntPtr]$script:playerInfo.Handle)) {
            Write-Output '- forcing a visibility change so the pending PERF batch is flushed'
            Set-AppForeground -Hwnd ([IntPtr]$script:playerInfo.Handle) | Out-Null
            Set-CursorInsideWindow -Hwnd ([IntPtr]$script:playerInfo.Handle) -X $script:revealX -Y $script:revealY
            Start-Sleep -Milliseconds 500
            Set-CursorInsideWindow -Hwnd ([IntPtr]$script:playerInfo.Handle) -X $script:tapX -Y $script:tapY
            Invoke-LeftClick $script:tapX $script:tapY
            Start-Sleep -Milliseconds 700
            Set-CursorInsideWindow -Hwnd ([IntPtr]$script:playerInfo.Handle) -X $script:revealX -Y $script:revealY
            Start-Sleep -Milliseconds 500
            Set-CursorInsideWindow -Hwnd ([IntPtr]$script:playerInfo.Handle) -X $script:tapX -Y $script:tapY
            Invoke-LeftClick $script:tapX $script:tapY
            Start-Sleep -Seconds 2
        } else {
            Write-Output '- player window is gone; reading the log without a flush nudge'
        }
    } else {
        Write-Output '- no cycles ran; reading the log without waiting for a flush'
    }

    $logPath = Get-LogPath -LocalState $localState
    $data = $null
    if ($null -eq $logPath) {
        Add-Failure '4' "no log-*.txt file under $localState\Logger"
        $exitCode = 2
    } else {
        Write-Output "- reading: $logPath"
        try {
            $data = Read-PerfLog -Path $logPath -Since $scriptStart
            Write-Output "- lines after start: $($data.LinesAfter), PERF lines: $($data.PerfLines.Count)"
        } catch {
            Add-Failure '4' "could not read the log: $($_.Exception.Message)"
            $exitCode = 2
        }
    }

    # ---- Step 5: report ----------------------------------------------------
    Write-Output ''
    Write-Output '## Step 5: summary'
    Write-Report -LogPath $logPath -Data $data -AttemptedCycles $script:cyclesRan

    $script:resultCode = $exitCode; return
}

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
$exitCode = 0
try {
    Write-Output "# sample-topbar.ps1"
    Write-Output "- started: $($scriptStart.ToString('yyyy-MM-dd HH:mm:ss.fff zzz'))"
    Write-Output "- cycles=$Cycles gapMs=$GapMs bvid=$Bvid skipLaunch=$($SkipLaunch.IsPresent)"
    Invoke-Sampling
    $exitCode = $script:resultCode
} catch {
    Add-Failure 'fatal' "unhandled error: $($_.Exception.Message)"
    Write-Output $_.ScriptStackTrace
    $exitCode = 1
} finally {
    if ($script:probePath -and (Test-Path -LiteralPath $script:probePath)) {
        try {
            Remove-Item -LiteralPath $script:probePath -Force
            Write-Output "- perf-probe.txt deleted: $($script:probePath)"
        } catch {
            Write-Output "- WARNING: could not delete perf-probe.txt ($($_.Exception.Message))"
        }
    } elseif ($script:probePath) {
        Write-Output "- perf-probe.txt already gone: $($script:probePath)"
    }
}

Write-Output "- exit code: $exitCode"
exit $exitCode
