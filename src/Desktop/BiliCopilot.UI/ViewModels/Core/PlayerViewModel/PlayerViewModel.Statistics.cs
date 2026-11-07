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
            if (_sourceResolver is VideoMediaSourceResolver videoResolver)
            {
                lines.AddRange(videoResolver.BuildStatistics());
            }
            else
            {
                lines.Add(new("状态", _sourceResolver is null ? "还没有开始播放" : "统计信息目前只对点播视频有效"));
            }

            // 网络活动直接用播放器已经在跟踪的缓存速度，实时且不额外发请求.
            var cacheSpeed = Player?.CacheSpeed ?? 0;
            lines.Add(new("网络活动", cacheSpeed < 1 ? "—" : $"{cacheSpeed / 1024d:F0} KB/s"));
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
}
