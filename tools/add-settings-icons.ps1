# 给设置卡片补 HeaderIcon 的小工具：按文件 + Header 文本查表，插到 <base:SettingsCard ...> 标签后面。
# 只动没有 HeaderIcon 的卡片，已有点图标的不碰。

$root = 'E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src\Desktop\BiliCopilot.UI\Controls\Settings'

# 文件 -> (Header 文本 -> Symbol)
$map = @{
    'AdSkipSettingControl.xaml' = @{
        '自动跳过' = 'PlayCircle'
        '片段数据服务器' = 'Globe'
        '手动模式' = 'Edit'
        '最短片段时长（秒）' = 'TopSpeed'
        '包含自我推广' = 'Reward'
        '包含片头' = 'FastForward'
        '包含片尾' = 'ArrowStepBack'
        '云端广告数据' = 'ArrowSync'
        '云端接口地址' = 'GlobeSearch'
        '分析观看兴趣' = 'PeopleEye'
        'AI 识别广告' = 'Sparkle'
        'AI 接口地址' = 'BranchFork'
        'AI 密钥' = 'LockClosed'
        'AI 模型' = 'ContentSettings'
        '识别提示词' = 'TextBulletList'
        '音频分析' = 'Speaker2'
        '阿里云密钥' = 'LockClosed'
        '转写模型' = 'DocumentBulletList'
        'AI 设置' = 'Settings'
        '自动保存输入内容' = 'Save'
        '配置状态' = 'Info'
        '状态' = 'Info'
    }
    'DownloadSettingControl.xaml' = @{
        'DefaultDownloadFolder' = 'FolderOpen'
        'DownloadWithDanmaku' = 'Comment'
        'SkipAISubtitle' = 'Subtitles'
        'OpenFolderWhenDownloaded' = 'FolderOpen'
        'UseExternalBBDown' = 'ArrowDownload'
        'CopyCommandWhenDownload' = 'Copy'
        'GenCommandWithoutCredential' = 'ClipboardTextEdit'
    }
    'LoggerSettingControl.xaml' = @{ 'LogLevel' = 'DocumentBulletList' }
    'MpvExtraSettingControl.xaml' = @{
        '记住着色器与超分设置' = 'Save'
        'D3D11Adapter' = 'Tv'
        'VulkanDevice' = 'Tv'
        'AudioExclusive' = 'Speaker2'
        'AudioChannelLayout' = 'Speaker2'
        'BuiltInProfileSwitch' = 'ArrowSwap'
        'HrSeekSetting' = 'FastForward'
        'MaxCacheSize' = 'Collections'
        'MaxBackCacheSize' = 'Collections'
        'MaxCacheSeconds' = 'CalendarClock'
        'CacheOnDisk' = 'Save'
        'CacheDirectory' = 'FolderOpen'
        'MaxVolume' = 'Speaker2'
    }
    'NotificationSettingControl.xaml' = @{ 'VideoDynamicNotification' = 'VideoClip' }
    'PlayerBehaviorSettingControl.xaml' = @{
        'HideMainWindowOnPlay' = 'ContractDownLeft'
        'UseIntegrationBehavior' = 'ArrowSwap'
        'PreferDecode' = 'VideoPlayPause'
        'CustomMpvConfig' = 'ContentSettings'
        'PreferQuality' = 'TopSpeed'
        'DefaultPlayerDisplayMode' = 'LayoutColumnTwo'
        'PlayNextWithoutTip' = 'Next'
        'AutoNextRelatedVideo' = 'Stream'
        'StopWhenEndOfPlaylist' = 'Dismiss'
        'NoP2P' = 'Globe'
        '使用网页播放器' = 'Globe'
        '播放失败自动回落' = 'ArrowUndo'
    }
    'PlayerControlSettingControl.xaml' = @{
        'PreferCodec' = 'VideoClip'
        'StepForwardSeconds' = 'FastForward'
        'StepBackwardSeconds' = 'Previous'
        'TemporaryPlaybackRate' = 'TopSpeed'
        'GlobalPlaybackRate' = 'TopSpeed'
        'BottomProgressVisible' = 'LayoutColumnTwo'
    }
    'ProxySettingControl.xaml' = @{
        '启用播放代理' = 'Globe'
        '首选代理服务器' = 'GlobeSearch'
        '自定义港澳代理服务器' = 'Globe'
        '自定义台湾代理服务器' = 'Globe'
        '自定义大陆代理服务器' = 'Globe'
        '替换 CDN 链接' = 'ArrowSwap'
        '替换模式' = 'ContentSettings'
        '自动优选与切换' = 'ArrowSync'
        '过滤异常端口（防 PCDN）' = 'LockClosed'
        '改写 Akamai 节点' = 'Edit'
        '卡顿自动切节点' = 'ArrowClockwise'
        '替换 CDN 服务器' = 'Globe'
        'CDN 节点池' = 'Collections'
        'CDN 节点延迟' = 'TopSpeed'
    }
    'QualityUnlockSettingControl.xaml' = @{
        '启用画质解锁' = 'VideoClip'
        '自动选择最高画质' = 'TopSpeed'
        '解析服务器' = 'Globe'
        '上游主页' = 'BranchFork'
        '同步状态' = 'ArrowSync'
    }
    'ScreenshotSettingControl.xaml' = @{
        'ScreenshotFolder' = 'FolderOpen'
        'ScreenshotPostBehavior' = 'Screenshot'
    }
    'ThemeSettingControl.xaml' = @{ '亚克力背景' = 'Blur' }
    'AboutSettingControl.xaml' = @{ '(动态绑定)' = 'Info' }
}

$report = @()

foreach ($file in $map.Keys) {
    $path = Join-Path $root $file
    if (-not (Test-Path $path)) { $report += "$file 不存在"; continue }

    $text = [System.IO.File]::ReadAllText($path)
    $symbols = $map[$file]
    $out = New-Object System.Text.StringBuilder
    $cursor = 0
    $changed = 0

    while ($true) {
        $idx = $text.IndexOf('<base:SettingsCard', $cursor)
        if ($idx -lt 0) { break }

        # 排除 <base:SettingsCard.HeaderIcon> / .Items 这类
        $after = $text.Substring($idx + 18, 1)
        if ($after -ne ' ' -and $after -ne "`r" -and $after -ne "`n" -and $after -ne '>') {
            $out.Append($text.Substring($cursor, ($idx + 18) - $cursor)) | Out-Null
            $cursor = $idx + 18
            continue
        }

        # 找起始标签的结束位置（跳过引号内的 >）
        $i = $idx + 18
        $inQuote = $false
        while ($i -lt $text.Length) {
            $ch = $text[$i]
            if ($ch -eq '"') { $inQuote = -not $inQuote }
            elseif ($ch -eq '>' -and -not $inQuote) { break }
            $i++
        }
        $tagEnd = $i                      # '>' 的位置
        $tagText = $text.Substring($idx, $tagEnd - $idx + 1)

        # 卡片内容范围：到下一个 <base:SettingsCard 为止
        $nextIdx = $text.IndexOf('<base:SettingsCard', $tagEnd)
        $chunkEnd = if ($nextIdx -lt 0) { $text.Length } else { $nextIdx }
        $chunk = $text.Substring($tagEnd + 1, $chunkEnd - $tagEnd - 1)

        $hasIcon = $chunk.Contains('<base:SettingsCard.HeaderIcon')

        # 取 Header 文本
        $header = ''
        $m = [regex]::Match($tagText, 'Header="([^"]*)"')
        if ($m.Success) {
            $raw = $m.Groups[1].Value
            $lm = [regex]::Match($raw, 'Name=([A-Za-z0-9_]+)')
            $header = if ($lm.Success) { $lm.Groups[1].Value } else { $raw }
        }
        if ([string]::IsNullOrEmpty($header)) { $header = '(动态绑定)' }

        if ($hasIcon -or -not $symbols.ContainsKey($header)) {
            $out.Append($text.Substring($cursor, $tagEnd + 1 - $cursor)) | Out-Null
            $cursor = $tagEnd + 1
            continue
        }

        $symbol = $symbols[$header]
        $selfClosing = $tagText.TrimEnd().EndsWith('/>')

        # 行缩进：取卡片所在行的前导空白
        $lineStart = $text.LastIndexOf("`n", $idx) + 1
        $indent = ''
        $p = $lineStart
        while ($p -lt $idx -and ($text[$p] -eq ' ' -or $text[$p] -eq "`t")) { $indent += $text[$p]; $p++ }
        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $inner = $indent + (if ($indent -match "`t") { "`t" } else { '    ' })

        $iconBlock = $nl + $inner + '<base:SettingsCard.HeaderIcon>' +
                     $nl + $inner + '    <ic:SymbolIcon Symbol="' + $symbol + '" />' +
                     $nl + $inner + '</base:SettingsCard.HeaderIcon>'

        if ($selfClosing) {
            $openTag = $tagText.Substring(0, $tagText.Length - 2).TrimEnd() + '>'
            $replacement = $openTag + $iconBlock + $nl + $indent + '</base:SettingsCard>'
        }
        else {
            $replacement = $tagText + $iconBlock
        }

        $out.Append($text.Substring($cursor, $idx - $cursor)) | Out-Null
        $out.Append($replacement) | Out-Null
        $cursor = $tagEnd + 1
        $changed++
    }

    $out.Append($text.Substring($cursor)) | Out-Null

    if ($changed -gt 0) {
        [System.IO.File]::WriteAllText($path, $out.ToString())
        $report += "$file 补了 $changed 个图标"
    }
    else {
        $report += "$file 没改（表里没匹配上）"
    }
}

$report | ForEach-Object { Write-Output $_ }