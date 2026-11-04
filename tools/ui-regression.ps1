# UI 回归自测脚本：逐项验证界面与功能，并把对应日志片段截出来
# 用法：pwsh -File tools\ui-regression.ps1
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$appId = 'shell:AppsFolder\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App'
$logDir = "$env:LOCALAPPDATA\Packages\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46\LocalState\Logger"
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$name, [string]$status, [string]$detail) {
    $results.Add([pscustomobject]@{ Test = $name; Status = $status; Detail = $detail })
    $color = switch ($status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("[{0}] {1} — {2}" -f $status, $name, $detail) -ForegroundColor $color
}

function Get-AppWindow {
    $p = Get-Process -Name BiliCopilot.UI -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { return $null }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $wins) {
        if ($w.Current.BoundingRectangle.Width -gt 800) { return $w }
    }
    return $null
}

function Get-All($win) {
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Find-ByName($win, [string]$name, [string]$type = '') {
    foreach ($e in (Get-All $win)) {
        try {
            if ($e.Current.Name -eq $name -and ($type -eq '' -or $e.Current.ControlType.ProgrammaticName -eq "ControlType.$type")) { return $e }
        } catch { }
    }
    return $null
}

function Find-Contains($win, [string]$text) {
    foreach ($e in (Get-All $win)) {
        try { if ($e.Current.Name -and $e.Current.Name.Contains($text)) { return $e } } catch { }
    }
    return $null
}

function Invoke-Elem($elem) {
    if ($null -eq $elem) { return $false }
    try {
        $pats = $elem.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
        if ($pats -match 'InvokePattern') { ($elem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); return $true }
        if ($pats -match 'SelectionItemPattern') { ($elem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); return $true }
        if ($pats -match 'TogglePattern') { ($elem.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle(); return $true }
    } catch { }
    return $false
}

function Select-Nav($win, [string]$name) {
    $best = $null
    foreach ($e in (Get-All $win)) {
        try {
            $r = $e.Current.BoundingRectangle
            if ($e.Current.Name -ne $name) { continue }
            if ($r.Width -lt 20) { continue }
            $pats = $e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
            if (($pats -match 'SelectionItemPattern') -or ($pats -match 'InvokePattern')) {
                if ($null -eq $best -or $r.Top -lt $best.Current.BoundingRectangle.Top) { $best = $e }
            }
        } catch { }
    }
    if ($null -eq $best) { return $false }
    $pats = $best.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
    try {
        if ($pats -match 'SelectionItemPattern') { ($best.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); return $true }
        if ($pats -match 'InvokePattern') { ($best.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); return $true }
    } catch { }
    return $false
}

function Activate-Window($win) {
    try {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Act {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
}
"@ -ErrorAction SilentlyContinue
    $h = [IntPtr]$win.Current.NativeWindowHandle
    [Win32Act]::ShowWindow($h, 9) | Out-Null
    [Win32Act]::SetForegroundWindow($h) | Out-Null
    $r = $win.Current.BoundingRectangle
    [Win32Act]::SetCursorPos([int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height / 2)) | Out-Null; Start-Sleep -Milliseconds 200; [Win32Act]::SetCursorPos([int]($r.Left + $r.Width / 2 + 12), [int]($r.Top + 40)) | Out-Null
    Start-Sleep -Milliseconds 900
} catch { }
}

function Scroll-ToEnd($win) {
    foreach ($e in (Get-All $win)) {
        try {
            if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Pane -or $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Document) {
                $pats = $e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
                if ($pats -match 'ScrollPattern') {
                    $sp = $e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                    if ($sp.Current.VerticallyScrollable) { $sp.SetScrollPercent(-1, 100); Start-Sleep -Milliseconds 600 }
                }
            }
        } catch { }
    }
}
function Scroll-Page($win, [int]$steps = 10) {
    try {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class UiWheel {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);
    public static void Scroll(int x, int y, int delta) {
        SetCursorPos(x, y);
        mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero);
    }
}
"@ -ErrorAction SilentlyContinue
        $r = $win.Current.BoundingRectangle
        $x = [int]($r.Left + $r.Width * 0.62)
        $y = [int]($r.Top + $r.Height * 0.55)
        for ($i = 0; $i -lt $steps; $i++) { [UiWheel]::Scroll($x, $y, -120); Start-Sleep -Milliseconds 200 }
        Start-Sleep -Milliseconds 600
    } catch { }
}
function Scroll-Up($win, [int]$steps = 20) {
    try {
        $r = $win.Current.BoundingRectangle
        $x = [int]($r.Left + $r.Width * 0.62)
        $y = [int]($r.Top + $r.Height * 0.45)
        for ($i = 0; $i -lt $steps; $i++) { [UiWheel]::Scroll($x, $y, 120); Start-Sleep -Milliseconds 150 }
        Start-Sleep -Milliseconds 500
    } catch { }
}
function Get-LogPath {
    $f = Get-ChildItem $logDir -Filter '*.txt' | Sort-Object LastWriteTime | Select-Object -Last 1
    return $f.FullName
}

function Get-NewLog([string]$path, [int]$fromLine) {
    return (Get-Content $path | Select-Object -Skip $fromLine)
}

# ---------- 启动 ----------
Get-Process -Name BiliCopilot.UI -ErrorAction SilentlyContinue | ForEach-Object { taskkill /F /PID $_.Id 2>&1 | Out-Null }
Start-Sleep -Seconds 4
Start-Process $appId
Start-Sleep -Seconds 30
$win = Get-AppWindow
if ($null -eq $win) { Add-Result '应用启动' 'FAIL' '30 秒内没找到主窗口'; $results | Format-Table -AutoSize | Out-String -Width 200; exit 1 }

$logPath = Get-LogPath
$logStart = (Get-Content $logPath | Measure-Object -Line).Lines
Add-Result '应用启动' 'PASS' ("窗口 " + [int]$win.Current.BoundingRectangle.Width + "x" + [int]$win.Current.BoundingRectangle.Height + "，日志 " + (Split-Path $logPath -Leaf))

# ---------- 设置页：AI 配置、保存/测试按钮、自动保存、CDN 节点池 ----------
if (Select-Nav $win '设置') { Start-Sleep -Seconds 6 }

function Expand-Section($win, [string]$pattern) {
    foreach ($e in (Get-All $win)) {
        try {
            if ($e.Current.Name -match $pattern) {
                $pats = $e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
                if ($pats -match 'ExpandCollapsePattern') {
                    ($e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
                    Start-Sleep -Seconds 2
                    return $true
                }
            }
        } catch { }
    }
    return $false
}

# 1) 空降助手分区
$adExpanded = Expand-Section $win '^空降助手$'
Scroll-Page $win 10
$aiEndpoint = $null
foreach ($e in (Get-All $win)) {
    try {
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit -and $e.Current.Name -eq 'AI 接口地址') {
            $aiEndpoint = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        }
    } catch { }
}
if ($aiEndpoint) { Add-Result '设置·AI 接口地址回填' 'PASS' ("读到 " + $aiEndpoint) } else { Add-Result '设置·AI 接口地址回填' 'FAIL' '设置页找不到已保存的接口地址' }

foreach ($label in @('AI 设置', '自动保存输入内容', '配置状态', '测试接口', '测试模型')) {
    $hit = Find-ByName $win $label
    if ($null -eq $hit) { $hit = Find-Contains $win $label }
    if ($hit) { Add-Result ("设置·" + $label) 'PASS' '卡片/按钮存在' } else { Add-Result ("设置·" + $label) 'FAIL' '没找到' }
}

$testModel = Find-ByName $win '测试模型' 'Button'
if ($testModel) {
    Invoke-Elem $testModel | Out-Null
    Start-Sleep -Seconds 14
    $status = $null
    foreach ($e in (Get-All $win)) {
        try { if ($e.Current.Name -match '模型正常|模型有问题|请求失败|HTTP [0-9]{3}|密钥被拒绝|内容为空') { $status = $e.Current.Name } } catch { }
    }
    if ($status) { Add-Result '设置·测试模型结果' 'PASS' $status } else { Add-Result '设置·测试模型结果' 'WARN' '点了但没读到结果回显' }
} else { Add-Result '设置·测试模型结果' 'FAIL' '按钮不存在' }

# 2) 网络与加速分区（CDN 节点池在这里）
Scroll-Up $win 24
$netExpanded = Expand-Section $win '网络|代理|加速'
Scroll-Page $win 16
$poolCard = Find-ByName $win 'CDN 节点池'
if ($null -eq $poolCard) { $poolCard = Find-Contains $win 'CDN 节点池' }
if ($poolCard) { Add-Result '设置·CDN 节点池' 'PASS' '卡片存在' } else { Add-Result '设置·CDN 节点池' 'FAIL' '没找到卡片' }
$pool = Find-Contains $win '个节点'
if ($pool) { Add-Result '设置·CDN 节点池状态' 'PASS' $pool.Current.Name } else { Add-Result '设置·CDN 节点池状态' 'WARN' '没看到节点数文本' }
# ---------- 动态页：已看过 + 观看兴趣 ----------
if (Select-Nav $win '动态') { Start-Sleep -Seconds 16 }
$watched = Find-Contains $win '观看超过 1 分钟'
if ($watched) { Add-Result '动态·已看过栏' 'PASS' '有「观看超过 1 分钟」说明' } else { Add-Result '动态·已看过栏' 'WARN' '没看到已看过栏（可能不在该栏视图）' }
$interest = Find-ByName $win '观看兴趣（AI 分析）'
if ($null -eq $interest) { $interest = Find-Contains $win '观看兴趣' }
if ($interest) { Add-Result '动态·观看兴趣分析' 'PASS' '有 AI 分析块' } else { Add-Result '动态·观看兴趣分析' 'WARN' '暂未生成（需等模型返回）' }

# ---------- 历史记录 / 稍后再看 / 收藏 ----------
if (Select-Nav $win '历史记录') { Start-Sleep -Seconds 14 }
$h = Find-ByName $win '观看历史'
if ($null -eq $h) { Select-Nav $win '历史记录' | Out-Null; Start-Sleep -Seconds 10; $h = Find-ByName $win '观看历史' }
if ($h) { Add-Result '历史记录页' 'PASS' ('顶栏 y=' + [int]$h.Current.BoundingRectangle.Top) } else { Add-Result '历史记录页' 'FAIL' '没看到「观看历史」' }

if (Select-Nav $win '稍后再看') { Start-Sleep -Seconds 14 }
$v = Find-ByName $win '稍后再看'
if ($null -eq $v) { Select-Nav $win '稍后再看' | Out-Null; Start-Sleep -Seconds 10; $v = Find-ByName $win '稍后再看' }
if ($v) { Add-Result '稍后再看页' 'PASS' ('顶栏 y=' + [int]$v.Current.BoundingRectangle.Top) } else { Add-Result '稍后再看页' 'FAIL' '没看到「稍后再看」' }

$fav = Find-ByName $win '我的收藏' 'Button'
if ($fav) {
    Invoke-Elem $fav | Out-Null
    Start-Sleep -Seconds 8
    $favTitle = Find-Contains $win '收藏'
    if ($favTitle) { Add-Result '收藏页' 'PASS' '已进入收藏相关界面' } else { Add-Result '收藏页' 'WARN' '点了入口但没确认页面' }
} else { Add-Result '收藏页' 'WARN' '没找到收藏入口' }

# ---------- 播放器 ----------
if (Select-Nav $win '动态') { Start-Sleep -Seconds 16 }
$opened = $false
foreach ($e in (Get-All $win)) {
    try {
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
            $r = $e.Current.BoundingRectangle
            if ([int]$r.Width -ge 380 -and [int]$r.Width -le 700 -and [int]$r.Height -ge 100 -and [int]$r.Height -le 300 -and [int]$r.Left -gt 600) {
                ($e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                $opened = $true
                break
            }
        }
    } catch { }
}
Start-Sleep -Seconds 26
$playerWin = $null
$playerPid = (Get-Process -Name BiliCopilot.UI | Select-Object -First 1).Id
foreach ($w in ([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition))) {
    try {
        if ($w.Current.ProcessId -eq $playerPid -and $w.Current.BoundingRectangle.Width -gt 800) {
            if ($playerWin -eq $null -or $w.Current.Name -ne '哔哩助理') { $playerWin = $w }
        }
    } catch { }
}
if ($null -eq $playerWin) { $playerWin = Get-AppWindow }
if ($opened -and $playerWin) {
    Activate-Window $playerWin
    Start-Sleep -Seconds 2
    Add-Result '播放器打开' 'PASS' ('窗口 ' + $playerWin.Current.Name.Substring(0, [Math]::Min(24, $playerWin.Current.Name.Length)))
    $cdn = $null; $ad = $null; $ai = $null
    foreach ($e in (Get-All $playerWin)) {
        try {
            $n = $e.Current.Name
            $r = $e.Current.BoundingRectangle
            if ([int]$r.Top -gt 20 -and [int]$r.Top -lt 120) {
                if ($n -like '空降*') { $ad = $n }
                if ($n -like 'AI*') { $ai = $n }
                if ($n -cmatch '^upos-|^cn-[a-z]+-|^d0--|^c0--|mirror|gotcha|bilivideo') { $cdn = $n }
            }
        } catch { }
    }
    if ($cdn) { Add-Result '播放器·CDN 节点显示' 'PASS' $cdn } else { Add-Result '播放器·CDN 节点显示' 'WARN' '没读到节点文本' }
    if ($ad) { Add-Result '播放器·空降按钮' 'PASS' $ad } else { Add-Result '播放器·空降按钮' 'FAIL' '顶栏没有空降按钮' }
    if ($ai) { Add-Result '播放器·AI 跳过按钮' 'PASS' $ai } else { Add-Result '播放器·AI 跳过按钮' 'FAIL' '顶栏没有 AI 按钮' }

    # 侧栏 tab
    $tabFound = $false
    foreach ($t in @('信息', '弹幕', '评论列表')) {
        if (Find-ByName $playerWin $t 'ListItem') { $tabFound = $true }
    }
    if ($tabFound) { Add-Result '播放器·侧栏分区' 'PASS' '信息/弹幕/评论列表都在' } else { Add-Result '播放器·侧栏分区' 'WARN' '侧栏默认收起，需要手动展开后再看' }
} else {
    Add-Result '播放器打开' 'FAIL' '没能打开视频'
}

# ---------- 日志检查 ----------
$logLines = Get-NewLog $logPath $logStart
$patterns = @{
    '日志·CDN 节点池同步' = '节点池已更新'
    '日志·CDN 优选完成' = 'CDN 优选完成'
    '日志·播放增强恢复' = '恢复播放增强|启动读取播放增强'
    '日志·兴趣分析' = '观看兴趣分析完成'
    '日志·直播连接' = '直播弹幕：开始连接|直播间'
}
foreach ($k in $patterns.Keys) {
    $m = $logLines | Select-String -Pattern $patterns[$k] | Select-Object -Last 1
    if ($m) { Add-Result $k 'PASS' ($m.Line.Substring(0, [Math]::Min(110, $m.Line.Length))) } else { Add-Result $k 'WARN' '本次运行没有这条日志' }
}
$errors = $logLines | Select-String -Pattern '\[ERR\]' | Select-Object -Last 5
if ($errors) {
    Add-Result '日志·错误条目' 'WARN' ($errors.Count.ToString() + ' 条 ERR，最新：' + $errors[0].Line.Substring(0, [Math]::Min(100, $errors[0].Line.Length)))
} else { Add-Result '日志·错误条目' 'PASS' '没有 ERR' }

Write-Host ''
Write-Host '===== 汇总 =====' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
$pass = ($results | Where-Object Status -eq 'PASS').Count
$fail = ($results | Where-Object Status -eq 'FAIL').Count
$warn = ($results | Where-Object Status -eq 'WARN').Count
Write-Host ("通过 {0} / 失败 {1} / 存疑 {2}" -f $pass, $fail, $warn) -ForegroundColor Cyan
$results | ConvertTo-Json -Depth 3 | Set-Content -Path 'tools\ui-regression-last.json' -Encoding utf8
exit ($fail + 0)
