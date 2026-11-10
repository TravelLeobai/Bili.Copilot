// Copyright (c) Bili Copilot. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using BiliCopilot.UI.Models.Constants;
using Microsoft.Extensions.Logging;
using BiliCopilot.UI.Toolkits;
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

    private bool _liveBehindSticky;

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
            // 拿不到滑动窗口长度（切源、布局重建的那一瞬间）时保留上一次的「落后」状态，
            // 否则「回到最新」按钮会跟着闪一下没掉.
            LiveLatencySeconds = 0;
            LiveEdgeRatio = 1;
            if (!_liveBehindSticky)
            {
                LiveLatencyText = "直播中";
            }

            return;
        }

        var latency = Math.Max(0, duration - position);
        LiveLatencySeconds = latency;
        LiveEdgeRatio = Math.Clamp(position / duration, 0, 1);

        // 带滞回：落后 3 秒就亮出来，回落到 1.5 秒内才收掉，避免在阈值附近来回闪.
        _liveBehindSticky = latency >= 3
            || (_liveBehindSticky && latency >= 1.5);
        IsLiveBehind = _liveBehindSticky;
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

    /// <summary>
    /// 点播时间轴（缓存条、进度条、章节）是否显示：直播时整块收掉.
    /// </summary>
    public bool IsVodTimelineVisible => !IsLive && !IsPreviewProgressChanging;

    partial void OnIsLiveChanged(bool value)
    {
        // 直播时把底部那条自动隐藏的细进度条也压掉.
        IsBottomProgressBarVisible = !value && !IsControlsVisible
            && SettingsToolkit.ReadLocalSetting(SettingNames.IsBottomProgressBarVisible, true);
        OnPropertyChanged(nameof(IsVodProgressVisible));
        OnPropertyChanged(nameof(IsVodTimelineVisible));
        OnPropertyChanged(nameof(IsCatchUpButtonVisible));
        OnPropertyChanged(nameof(LivePanelInsetThickness));
            // 覆盖层底部控制栏/进度条用的是 LivePanelAvoidanceThickness，
            // 不通知它的话宽窗下控制栏右侧那排按钮会被面板压住.
            OnPropertyChanged(nameof(LivePanelAvoidanceThickness));
        if (!value)
        {
            // 离开直播时把让位清零，别把点播界面也挤窄.
            LivePanelInset = 0;
        }
    }

    partial void OnIsPreviewProgressChangingChanged(bool value)
        => OnPropertyChanged(nameof(IsVodTimelineVisible));
    /// <summary>
    /// 直播面板展开时右侧让出的宽度（像素），0 表示不遮挡.
    /// </summary>
    public double LivePanelInset
    {
        get => _livePanelInset;
        set
        {
            if (Math.Abs(_livePanelInset - value) < 0.5)
            {
                return;
            }

            _livePanelInset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LivePanelInsetThickness));
        }
    }

    /// <summary>
    /// 给覆盖层用的让位边距：只有直播才让位，点播/番剧保持整窗布局.
    /// </summary>
    public Thickness LivePanelInsetThickness
        => IsLive ? new Thickness(0, 0, _livePanelInset, 0) : default;

    /// <summary>
    /// 窄窗时直播面板改成上下排布（画面在上面、面板在下面）.
    /// </summary>
    public bool IsLivePanelNarrow
    {
        get => _isLivePanelNarrow;
        private set
        {
            if (_isLivePanelNarrow == value)
            {
                return;
            }

            _isLivePanelNarrow = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LivePanelAvoidanceThickness));
        }
    }

    /// <summary>
    /// 窄窗时面板在底部占掉的高度（DIP）.
    /// </summary>
    public double LivePanelBottomInset => _livePanelBottomInset;

    /// <summary>
    /// 覆盖层要让位的边距：窄窗让底部，宽窗让右侧.
    /// </summary>
    public Thickness LivePanelAvoidanceThickness
        => IsLive
            ? (_isLivePanelNarrow
                ? new Thickness(0, 0, 0, _livePanelBottomInset)
                : new Thickness(0, 0, _livePanelInset, 0))
            : default;

    private bool _isLivePanelNarrow;

    private double _livePanelBottomInset;

    private double _livePanelInset;

    /// <summary>
    /// 由覆盖层在自适应状态切换时调用：窄窗 = 面板在底部，宽窗 = 面板在右侧.
    /// 窄窗下底部占位按「窗口高 - 16:9 画面高」算，画面正好贴顶、面板无缝接在下面.
    /// </summary>
    /// <param name="narrow">是否窄窗排布.</param>
    /// <param name="windowWidth">播放窗口宽度（DIP）.</param>
    /// <param name="windowHeight">播放窗口高度（DIP）.</param>
    public void SetLivePanelNarrow(bool narrow, double windowWidth, double windowHeight)
    {
        IsLivePanelNarrow = narrow;

        var bottom = 0d;
        if (narrow && windowWidth > 0 && windowHeight > 0)
        {
            // 直播源基本都是 16:9：画面高度按宽度换算，最多占 62% 高度，剩下全给面板.
            var videoHeight = Math.Min(windowWidth * 9d / 16d, windowHeight * 0.62d);
            bottom = Math.Max(120d, windowHeight - videoHeight);
        }

        if (Math.Abs(_livePanelBottomInset - bottom) >= 0.5)
        {
            _livePanelBottomInset = bottom;
            OnPropertyChanged(nameof(LivePanelBottomInset));
        }

        OnPropertyChanged(nameof(LivePanelAvoidanceThickness));
    }

    /// <summary>
    /// 悬浮的「回到最新」：控制栏自动隐藏时也能点，直播并落后时才出现.
    /// </summary>
    public bool IsCatchUpButtonVisible => IsLive && IsLiveBehind;

    partial void OnIsLiveBehindChanged(bool value)
        => OnPropertyChanged(nameof(IsCatchUpButtonVisible));
}
