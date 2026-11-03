# 直播播放自测：导航到"直播" → 点击推荐直播卡片缩略图 → 断言出现播放器窗口（MPV）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;using System.Runtime.InteropServices;using System.Text;
public class LV2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
}
"@

$bin = 'E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src\_build\x64\Debug\BiliCopilot.UI\bin'
$pfn = 'Richasy.BiliCopilot.Dev_g7ew4fxhv1f46'

Get-Process -Name BiliCopilot.UI -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }
Start-Sleep -Seconds 2
Add-AppxPackage -Register (Join-Path $bin 'AppxManifest.xml') -ForceUpdateFromAnyVersion -ErrorAction SilentlyContinue
Start-Process "shell:AppsFolder\$pfn!App"

function Get-MainWindow {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '哔哩助理')))
}

$win = $null
for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Seconds 2; $win = Get-MainWindow; if ($win) { break } }
if (-not $win) { Write-Output 'WINDOW_NOT_FOUND'; exit 1 }

# 1) 导航到直播
$liveItem = $null
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
foreach ($i in $items) {
    if ($i.Current.Name -eq '直播') {
        $r = $i.Current.BoundingRectangle
        if (-not [double]::IsInfinity($r.Left) -and ($r.Left - $win.Current.BoundingRectangle.Left) -lt 250) { $liveItem = $i; break }
    }
}
if (-not $liveItem) { Write-Output 'LIVE_NAV_NOT_FOUND'; exit 1 }
($liveItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Start-Sleep -Seconds 8

# 2) 收集推荐直播卡片的标题（窗口内 x>窗口宽度50%）
$win = Get-MainWindow
$wr = $win.Current.BoundingRectangle
$half = $wr.Width / 2
$cands = @()
$texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
foreach ($t in $texts) {
    $r = $t.Current.BoundingRectangle
    if ([double]::IsInfinity($r.Left) -or [double]::IsInfinity($r.Top) -or [double]::IsNaN($r.Left) -or [double]::IsNaN($r.Top)) { continue }
    $relx = $r.Left - $wr.Left
    $rely = $r.Top - $wr.Top
    $n = $t.Current.Name
    if ($relx -gt $half -and $r.Width -gt 150 -and $rely -gt 200 -and $rely -lt ($wr.Height - 120) -and $n.Length -gt 4 -and $n -notmatch '刷新|推荐的直播间|关注的直播间') {
        $cands += [pscustomobject]@{ Name = $n; X = [int]($r.Left + $r.Width / 2); Y = [int]$r.Top; Rely = [int]$rely }
    }
}
$cands = $cands | Sort-Object Rely -Unique
Write-Output ("candidates: " + $cands.Count)
$cands | Select-Object -First 3 | ForEach-Object { Write-Output ("  y=$($_.Rely) " + $_.Name) }
if ($cands.Count -eq 0) { Write-Output 'NO_LIVE_CARD'; exit 1 }

# 3) 前台化 + 逐张点击缩略图（标题上方 180px）
$proc = Get-Process -Name BiliCopilot.UI | Select-Object -First 1
$h = $proc.MainWindowHandle
$tmp = 0
$tid = [LV2]::GetWindowThreadProcessId($h, [ref]$tmp)
$cur = [LV2]::GetCurrentThreadId()
[LV2]::AttachThreadInput($cur, $tid, $true) | Out-Null
[LV2]::SetForegroundWindow($h) | Out-Null
[LV2]::AttachThreadInput($cur, $tid, $false) | Out-Null
Start-Sleep -Milliseconds 500

function Get-PlayerWindow {
    $pp = Get-Process -Name BiliCopilot.UI -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $pp) { return $null }
    $ids = @($pp.Id)
    $script:pw = @()
    $cb = [LV2+EnumWindowsProc]{ param($hw, $l)
        $q = 0
        [LV2]::GetWindowThreadProcessId($hw, [ref]$q) | Out-Null
        if ($ids -contains $q -and [LV2]::IsWindowVisible($hw)) {
            $sb = New-Object System.Text.StringBuilder 256
            [LV2]::GetWindowText($hw, $sb, 256) | Out-Null
            if ($sb.Length -gt 0) { $script:pw += $sb.ToString() }
        }
        return $true
    }
    [LV2]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    return ($script:pw | Where-Object { $_ -ne '哔哩助理' -and $_ -notmatch 'IME|MSCTF|DDE|SMTC' } | Select-Object -First 1)
}

$player = $null
foreach ($cand in ($cands | Select-Object -First 3)) {
    $cx = $cand.X
    $cy = [Math]::Max($cand.Y - 180, $wr.Top + 150)
    [LV2]::SetCursorPos($cx, $cy) | Out-Null
    Start-Sleep -Milliseconds 350
    [LV2]::mouse_event(0x02, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [LV2]::mouse_event(0x04, 0, 0, 0, [IntPtr]::Zero)
    Write-Output "clicked at $cx,$cy ($($cand.Name))"
    Start-Sleep -Seconds 9
    $player = Get-PlayerWindow
    if ($player) { break }
}

$proc = Get-Process -Name BiliCopilot.UI -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'APP_CRASHED_AFTER_CLICK'; exit 1 }
Write-Output ("player window: " + $player)

$bmp = New-Object System.Drawing.Bitmap(1400, 1060)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $env:TEMP 'live-verify.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Get-Process -Name BiliCopilot.UI -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }

if (-not $player) { Write-Output 'NO_PLAYER_WINDOW'; exit 1 }
Write-Output ('LIVE_OK ' + $player)
exit 0
