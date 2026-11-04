// Copyright (c) Bili Copilot. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 直播进度：按「离最新直播进度差多少」来显示，没有时间戳.
/// </summary>
public sealed partial class PlayerViewModel
{
    [ObservableProperty]
    private double _liveLatencySeconds;

    [ObservableProperty]
    private string _liveLatencyText = "直播中";

    [ObservableProperty]
    private bool _isLiveBehind;

    /// <summary>
    /// 当前进度在可播放窗口里的位置，1 = 贴合最新.
    /// </summary>
    [ObservableProperty]
    private double _liveEdgeRatio = 1d;

    [ObservableProperty]
    private bool _isLiveSeekable;

    /// <summary>
    /// 刷新直播延迟显示（由每秒的计时器驱动）.
    /// </summary>
    private void UpdateLiveLatency()
    {
        if (!IsLive)
        {
            return;
        }

        var duration = Player?.Duration ?? 0;
        var position = Player?.Position ?? 0;
        IsLiveSeekable = duration > 1;

        if (duration <= 1)
        {
            // 拿不到滑动窗口长度时只能说明是直播，不给拖动.
            LiveLatencySeconds = 0;
            LiveEdgeRatio = 1;
            IsLiveBehind = false;
            LiveLatencyText = "直播中";
            return;
        }

        var latency = Math.Max(0, duration - position);
        LiveLatencySeconds = latency;
        LiveEdgeRatio = Math.Clamp(position / duration, 0, 1);
        IsLiveBehind = latency > 8;
        LiveLatencyText = latency switch
        {
            < 3 => "实时",
            < 60 => $"落后 {latency:F0} 秒",
            _ => $"落后 {(int)(latency / 60)} 分 {latency % 60:F0} 秒",
        };
    }

    /// <summary>
    /// 跳回最新直播进度.
    /// </summary>
    [RelayCommand]
    private async Task CatchUpToLiveAsync()
    {
        var duration = Player?.Duration ?? 0;
        if (duration <= 1)
        {
            return;
        }

        await ApplySeekAsync(Math.Max(0, duration - 0.4));
        logger.LogInformation("直播已追到最新进度：{Position:F1}/{Duration:F1}", Player?.Position ?? 0, duration);
        UpdateLiveLatency();
    }

    /// <summary>
    /// 拖动直播进度条：value 是 0~1，1 表示最新.
    /// </summary>
    /// <param name="ratio">目标比例.</param>
    public async Task SeekLiveRatioAsync(double ratio)
    {
        var duration = Player?.Duration ?? 0;
        if (duration <= 1)
        {
            return;
        }

        var target = Math.Clamp(ratio, 0, 1) * duration;
        // 拖到最右端时留一点余量，避免正好卡在最后一帧.
        if (ratio >= 0.995)
        {
            target = Math.Max(0, duration - 0.4);
        }

        await ApplySeekAsync(target);
        UpdateLiveLatency();
    }
    /// <summary>
    /// 点播进度条是否显示：直播时不显示，避免和直播延迟条叠成两条.
    /// </summary>
    public bool IsVodProgressVisible => !IsLive;

    partial void OnIsLiveChanged(bool value)
    {
        // 直播时把底部那条自动隐藏的细进度条也压掉.
        IsBottomProgressBarVisible = !value && !IsControlsVisible
            && SettingsToolkit.ReadLocalSetting(SettingNames.IsBottomProgressBarVisible, true);
        OnPropertyChanged(nameof(IsVodProgressVisible));
    }
}
