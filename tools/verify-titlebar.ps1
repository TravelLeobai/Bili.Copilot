# 标题栏汉堡按钮几何自测：断言扁平按钮尺寸与对齐（物理像素，DPI 144 → scale 1.5）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$bin = 'E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src\_build\x64\Debug\BiliCopilot.UI\bin'
$pfn = 'Richasy.BiliCopilot.Dev_g7ew4fxhv1f46'

Get-Process -Name BiliCopilot.UI -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }
Start-Sleep -Seconds 2

if (-not (Test-Path (Join-Path $bin 'AppxManifest.xml'))) {
    Write-Output 'MISSING AppxManifest.xml in build output'
    exit 1
}

Add-AppxPackage -Register (Join-Path $bin 'AppxManifest.xml') -ForceUpdateFromAnyVersion -ErrorAction SilentlyContinue
Start-Process "shell:AppsFolder\$pfn!App"

$win = $null
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 2
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '哔哩助理')))
    if ($win) { break }
}
if (-not $win) { Write-Output 'WINDOW_NOT_FOUND'; exit 1 }

$wr = $win.Current.BoundingRectangle
$fail = @()

$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '切换导航栏')))
$nav = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '动态')))
$title = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '哔哩助理')))
if (-not $btn) { Write-Output 'BUTTON_NOT_FOUND'; exit 1 }
if (-not $nav) { Write-Output 'NAV_ITEM_NOT_FOUND'; exit 1 }

$br = $btn.Current.BoundingRectangle
$nr = $nav.Current.BoundingRectangle
$bx = [int]($br.Left - $wr.Left); $by = [int]($br.Top - $wr.Top); $bw = [int]$br.Width; $bh = [int]$br.Height
$bcx = $bx + [int]($br.Width / 2)
$navcx = [int]($nr.Left - $wr.Left) + 30   # 导航项行起点 18 + 图标中心偏移 30（实测基准）
Write-Output "button x=$bx y=$by w=$bw h=$bh cx=$bcx ; navRowX=$([int]($nr.Left - $wr.Left)) navIconCx~$navcx"

if ($bw -gt 66) { $fail += "button too wide: $bw" }
if ($bh -gt 66) { $fail += "button too tall: $bh" }
if ([math]::Abs($bcx - $navcx) -gt 8) { $fail += "icon center $bcx not aligned with nav icon center $navcx" }
if ($by -lt 0 -or $by -gt 72) { $fail += "button vertical offset $by outside title bar" }

if ($title) {
    $tr = $title.Current.BoundingRectangle
    $tx = [int]($tr.Left - $wr.Left)
    $gap = $tx - ($bx + $bw)
    Write-Output "title x=$tx gap=$gap"
    if ($gap -lt -2 -or $gap -gt 20) { $fail += "title gap $gap out of range" }
}

Get-Process -Name BiliCopilot.UI -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }

if ($fail.Count -gt 0) {
    $fail | ForEach-Object { Write-Output "FAIL: $_" }
    exit 1
}
Write-Output 'TITLEBAR_OK'
exit 0
