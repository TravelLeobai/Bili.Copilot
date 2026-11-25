// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Resolvers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型 - 统计信息浮层.
/// 右键菜单里的「属性」打开，浮层可以拖动、可以关掉，播放不受影响.
/// </summary>
public sealed partial class PlayerViewModel
{
    private DispatcherQueueTimer? _statisticsTimer;
    private bool _statsProbeReady;
    private bool _isExportingDiagnostics;

    /// <summary>
    /// 统计浮层是否显示.
    /// </summary>
    [ObservableProperty]
    private bool _isStatisticsVisible;

    /// <summary>
    /// 统计浮层里的行.
    /// </summary>
    public ObservableCollection<PlayerStatisticItem> StatisticsLines { get; } = new();

    /// <summary>
    /// 诊断区下面那行提示：导出结果或目录情况.
    /// </summary>
    [ObservableProperty]
    private string _diagnosticsNote = string.Empty;

    /// <summary>
    /// 打开播放停滞 dump 目录.
    /// </summary>
    [RelayCommand]
    private async Task OpenDumpFolderAsync()
    {
        var folder = Services.Diagnostics.DiagnosticsReport.DumpFolder;
        if (!Directory.Exists(folder))
        {
            DiagnosticsNote = "还没有 dump 目录：Debug 下判据命中后才会创建。";
            return;
        }

        await Services.Diagnostics.DiagnosticsReport.RevealAsync(folder);
        DiagnosticsNote = $"已打开 {folder}";
    }

    /// <summary>
    /// 打开日志目录.
    /// </summary>
    [RelayCommand]
    private async Task OpenLoggerFolderAsync()
    {
        var folder = Services.Diagnostics.DiagnosticsReport.LoggerFolder;
        if (!Directory.Exists(folder))
        {
            DiagnosticsNote = "还没有日志目录。";
            return;
        }

        await Services.Diagnostics.DiagnosticsReport.RevealAsync(folder);
        DiagnosticsNote = $"已打开 {folder}";
    }

    /// <summary>
    /// 把近几天的日志与停滞 dump 打成一个 zip 放到桌面，并在资源管理器里定位它.
    /// </summary>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (_isExportingDiagnostics)
        {
            return;
        }

        _isExportingDiagnostics = true;
        DiagnosticsNote = "正在打包…";
        try
        {
            var result = await Services.Diagnostics.DiagnosticsReport.ExportAsync(CancellationToken.None);
            var skipped = result.SkippedCount > 0 ? $"，跳过 {result.SkippedCount} 个占用中的文件" : string.Empty;
            DiagnosticsNote = $"已导出到桌面：{Path.GetFileName(result.Path)}（日志 {result.LogCount} 个、dump {result.DumpCount} 个，{FormatBytes(result.Bytes)}{skipped}）";
            await Services.Diagnostics.DiagnosticsReport.RevealAsync(result.Path);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("导出诊断包失败：{Type} {Message}", ex.GetType().Name, ex.Message);
            DiagnosticsNote = $"导出失败（{ex.GetType().Name}）。可以先用「打开日志目录」手动取。";
        }
        finally
        {
            _isExportingDiagnostics = false;
        }
    }

    /// <summary>
    /// 打开 / 收起统计浮层.
    /// </summary>
    [RelayCommand]
    private async Task ToggleStatisticsAsync()
    {
        IsStatisticsVisible = !IsStatisticsVisible;
        if (!IsStatisticsVisible)
        {
            StopStatisticsTimer();
            return;
        }

        await RefreshStatisticsAsync();
        StartStatisticsTimer();
    }

    /// <summary>
    /// 统计浮层第一次打开时把 mpv 探针脚本挂上，之后每秒读一次它写出来的值.
    /// </summary>
    private async Task EnsureStatsProbeAsync()
    {
        if (_statsProbeReady)
        {
            return;
        }

        _statsProbeReady = await Services.MpvStatsProbe.EnsureLoadedAsync(Client);
    }

    /// <summary>
    /// 关掉统计浮层.
    /// </summary>
    [RelayCommand]
    private void CloseStatistics()
    {
        IsStatisticsVisible = false;
        StopStatisticsTimer();
    }

    private async Task RefreshStatisticsAsync()
    {
        var lines = new List<PlayerStatisticItem>();
        try
        {
            await EnsureStatsProbeAsync();

            if (_sourceResolver is VideoMediaSourceResolver videoResolver)
            {
                lines.AddRange(videoResolver.BuildStatistics());
            }
            else
            {
                lines.Add(new("状态", _sourceResolver is null ? "还没有开始播放" : "统计信息目前只对点播视频有效"));
            }

            AppendEnhancementStatistics(lines);

            // 缓冲实况：面板上要的是「实际」而不是设置里的上限，所以单独一组.
            AppendCacheStatistics(lines);

            // Lua 探针写出来的实时值：码率、丢帧、帧率.
            var probe = Services.MpvStatsProbe.Read();
            if (probe is { } stats)
            {
                lines.Add(new("视频码率（实时）", stats.VideoBitrate > 0 ? $"{stats.VideoBitrate / 1000d:F0} Kbps" : "—"));
                lines.Add(new("音频码率（实时）", stats.AudioBitrate > 0 ? $"{stats.AudioBitrate / 1000d:F0} Kbps" : "—"));
                lines.Add(new("丢帧", stats.Dropped >= 0 ? $"{stats.Dropped:F0}" : "—"));
                lines.Add(new("帧率", stats.Fps > 0 ? $"{stats.Fps:F2} fps" : "—"));
            }
            else
            {
                lines.Add(new("mpv 实时统计", "还没读到（打开面板后约 1 秒出数）"));
            }

            // 网络活动直接用播放器已经在跟踪的缓存速度，实时且不额外发请求.
            var cacheSpeed = Player?.CacheSpeed ?? 0;
            lines.Add(new("网络活动", cacheSpeed < 1 ? "—" : $"{cacheSpeed / 1024d:F0} KB/s"));

            AppendSponsorStatistics(lines);
            AppendDiagnosticsStatistics(lines);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "刷新统计信息失败");
            lines.Add(new("状态", "读取统计信息失败"));
        }

        StatisticsLines.Clear();
        foreach (var line in lines)
        {
            StatisticsLines.Add(line);
        }
    }

    private void StartStatisticsTimer()
    {
        if (_statisticsTimer is null)
        {
            _statisticsTimer = this.Get<DispatcherQueue>().CreateTimer();
            _statisticsTimer.Interval = TimeSpan.FromSeconds(1);
            _statisticsTimer.IsRepeating = true;
            _statisticsTimer.Tick += async (_, _) => await RefreshStatisticsAsync();
        }

        _statisticsTimer.Start();
    }

    private void StopStatisticsTimer()
    {
        _statisticsTimer?.Stop();
    }

    /// <summary>
    /// 缓冲实况.
    /// <para>
    /// 数字来源：前向/磁盘字节、可跳区间、bof/eof 来自 <c>demuxer-cache-state</c>（内核解析后通过
    /// CacheStateChanged 送过来），缓存时长与缓冲进度走属性桥现查.
    /// 读不到的项明确写「不支持」，<b>不拿设置里的上限顶替</b> —— 面板要能看出
    /// 「配置 300MiB、实际只有 12MiB」这种差别.
    /// </para>
    /// <para>刷新由统计浮层的 1 秒定时器驱动（≤2Hz），这里只做一次快照读取.</para>
    /// </summary>
    /// <param name="lines">统计行.</param>
    private void AppendCacheStatistics(List<PlayerStatisticItem> lines)
    {
        Services.Mpv.MpvCacheSnapshot snapshot;
        try
        {
            snapshot = _cacheStateReader.Read(_cacheBridge);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "读取缓冲实况失败");
            lines.Add(new("缓冲实况", "读取失败"));
            return;
        }

        if (!snapshot.HasEventData)
        {
            lines.Add(new("缓冲实况", "还没收到 demuxer-cache-state（未开始播放，或 mpv 没有推送）"));
        }
        else
        {
            lines.Add(new("前向缓存（实际）", FormatBytes(snapshot.ForwardBytes)));
            lines.Add(new("磁盘缓存（实际）", snapshot.FileCacheBytes > 0 ? FormatBytes(snapshot.FileCacheBytes) : "未使用"));

            if (snapshot.SeekableRanges.Count == 0)
            {
                // 本地文件整段可跳，mpv 不上报区间；网络流才有可跳片段.
                lines.Add(new("可跳区间", "未上报（本地文件通常整段可跳）"));
            }
            else
            {
                var shown = string.Join(
                    "、",
                    snapshot.SeekableRanges.Take(3).Select(range => $"{FormatSkipStamp(range.Start)}–{FormatSkipStamp(range.End)}"));
                if (snapshot.SeekableRanges.Count > 3)
                {
                    shown += $"…共 {snapshot.SeekableRanges.Count} 段";
                }

                lines.Add(new("可跳区间", shown));
            }

            lines.Add(new(
                "已缓到两端",
                snapshot.BofCached && snapshot.EofCached
                    ? "是（整段已缓冲）"
                    : $"开头 {(snapshot.BofCached ? "是" : "否")} / 结尾 {(snapshot.EofCached ? "是" : "否")}"));
        }

        lines.Add(new(
            "缓冲中",
            snapshot.BufferingPercent is { } percent
                ? (percent < 100 ? $"是（{percent}%）" : "否")
                : "不支持（cache-buffering-state 读不到）"));

        lines.Add(new(
            "缓存时长（实际）",
            snapshot.CacheDurationSeconds is { } duration ? $"{duration:0.0} 秒" : "不支持（demuxer-cache-duration 读不到）"));
    }

    /// <summary>
    /// 着色器与英伟达超分的当前状态.
    /// 状态取自播放器上的这几个枚举，和右键菜单里勾着的一致.
    /// </summary>
    private void AppendEnhancementStatistics(List<PlayerStatisticItem> lines)
    {
        if (IsLive)
        {
            lines.Add(new("着色器", "不适用（直播）"));
            lines.Add(new("英伟达超分", "不适用（直播）"));
            return;
        }

        var shaders = new List<string>();
        AddShader(shaders, "Anime4K", Anime4KMode.ToString());
        AddShader(shaders, "ArtCNN", ArtCNNMode.ToString());
        AddShader(shaders, "NNEDI3", Nnedi3Mode.ToString());
        AddShader(shaders, "RAVU", RavuMode.ToString());

        var shaderText = shaders.Count == 0 ? "未启用" : string.Join(" + ", shaders);
        if (IsEnhancementRecovered)
        {
            shaderText += "（本次已回退）";
        }

        lines.Add(new("着色器", shaderText));
        lines.Add(new("英伟达超分", DescribeVsr(Models.Constants.VsrScale.None == VsrScale ? "None" : VsrScale.ToString(), IsEnhancementRecovered)));
    }

    private static void AddShader(List<string> target, string name, string? value)
    {
        if (string.IsNullOrEmpty(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        target.Add($"{name}={value}");
    }

    private static string DescribeVsr(string? value, bool rolledBack)
    {
        if (string.IsNullOrEmpty(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
        {
            return rolledBack ? "未启用（本次已回退）" : "未启用";
        }

        var label = value.ToLowerInvariant() switch
        {
            "x1_5" => "1.5x",
            "x2_0" => "2x",
            "x3_0" => "3x",
            "x4_0" => "4x",
            _ => value,
        };
        return rolledBack ? $"{label}（本次已回退）" : label;
    }

    /// <summary>
    /// 停滞 dump 与日志的概况。这两项只读文件、不发请求，放进来是为了让「属性」能直接看到诊断材料在哪。
    /// </summary>
    private static void AppendDiagnosticsStatistics(List<PlayerStatisticItem> lines)
    {
        var dumps = Services.Diagnostics.DiagnosticsReport.GetDumps();
        if (dumps.Count == 0)
        {
            lines.Add(new("播放停滞 dump", "无（Release 不采集；Debug 下命中判据才写）"));
        }
        else
        {
            var bytes = dumps.Sum(file => file.Length);
            var newest = dumps[0].LastWriteTime.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            lines.Add(new("播放停滞 dump", $"{dumps.Count} 个 · {FormatBytes(bytes)} · 最近 {newest}"));
        }

        lines.Add(new("dump 目录", Services.Diagnostics.DiagnosticsReport.DumpFolder));

        var logs = Services.Diagnostics.DiagnosticsReport.GetRecentLogs();
        var logBytes = logs.Sum(file => file.Length);
        lines.Add(new(
            "日志",
            logs.Count == 0
                ? $"近 {Services.Diagnostics.DiagnosticsReport.LogWindow.TotalDays:0} 天没有日志"
                : $"近 {Services.Diagnostics.DiagnosticsReport.LogWindow.TotalDays:0} 天 {logs.Count} 个 · {FormatBytes(logBytes)}"));
    }

    /// <summary>
    /// 把字节数变成人看的大小.
    /// </summary>
    /// <param name="bytes">字节数.</param>
    /// <returns>带单位的字符串.</returns>
    private static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024L ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0.0} KB";

    /// <summary>
    /// 把广告片段的三个字段接到统计面板后面：有没有、时间戳、识别方式.
    /// </summary>
    private void AppendSponsorStatistics(List<PlayerStatisticItem> lines)
    {
        var segments = _sponsorSegments ?? [];
        if (segments.Count == 0)
        {
            lines.Add(new("广告片段", "没有"));
            return;
        }

        lines.Add(new("广告片段", $"有 {segments.Count} 段"));

        var ordered = segments.OrderBy(p => p.Start).ToList();
        const int maxShow = 6;
        var stamps = string.Join(
            "、",
            ordered.Take(maxShow).Select(p => $"{FormatSkipStamp(p.Start)}–{FormatSkipStamp(p.End)}"));
        if (ordered.Count > maxShow)
        {
            stamps += $"…共 {ordered.Count} 段";
        }

        lines.Add(new("广告时间戳", stamps));
        lines.Add(new(
            "识别方式",
            string.Join(" + ", ordered.Select(p => DescribeSponsorSource(p.Source)).Distinct())));
    }

    /// <summary>
    /// 片段来源转成人能看懂的说法：来自空降助手服务器，还是本地 AI 识别.
    /// </summary>
    private static string DescribeSponsorSource(string? source)
        => (source ?? string.Empty).ToLowerInvariant() switch
        {
            "sponsorblock" => "空降助手服务器（SponsorBlock）",
            "cloud" => "云端广告数据",
            "ai" => "AI 识别（字幕 / 音频）",
            "test" => "测试数据",
            "" => "未知来源",
            _ => source!,
        };
}

