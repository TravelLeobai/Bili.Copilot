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

