// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 动态条目视图模型.
/// </summary>
public sealed partial class MomentItemViewModel
{
    private readonly IMomentOperationService _operationService;
    private readonly ILogger<MomentItemViewModel> _logger;
    private readonly Action<MomentItemViewModel> _showMomentAction;
    private readonly Action<MomentItemViewModel>? _openedAction;

    [ObservableProperty]
    private double? _likeCount;

    [ObservableProperty]
    private double? _commentCount;

    [ObservableProperty]
    private bool _isLiked;

    [ObservableProperty]
    private bool _isPgc;

    [ObservableProperty]
    private bool _isLastOpened;

    [ObservableProperty]
    private MomentVideoOpenedMarkStyle _openedMarkStyle;

    /// <summary>
    /// 作者.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// 头像.
    /// </summary>
    public Uri? Avatar { get; init; }

    /// <summary>
    /// 副标题.
    /// </summary>
    public string? Tip { get; init; }

    /// <summary>
    /// 视频封面.
    /// </summary>
    public Uri? VideoCover { get; init; }

    /// <summary>
    /// 视频时长.
    /// </summary>
    public string? VideoDuration { get; init; }

    /// <summary>
    /// 说明.
    /// </summary>
    public EmoteText? Description { get; init; }

    /// <summary>
    /// 视频标题.
    /// </summary>
    public string VideoTitle { get; init; }

    /// <summary>
    /// 是否没有数据.
    /// </summary>
    public bool NoData { get; init; }

    /// <summary>
    /// 内部内容.
    /// </summary>
    public object? InnerContent { get; init; }

    /// <summary>
    /// 卡片样式.
    /// </summary>
    public MomentCardStyle Style { get; init; }

    /// <summary>
    /// 用于匹配观看记录的媒体标识.
    /// </summary>
    public string? WatchHistoryKey => FindInnerContent<VideoInformation>() is { } video
        ? $"video_{video.Identifier.Id}"
        : FindInnerContent<EpisodeInformation>() is { } episode && episode.Identifier.Id != "0"
            ? $"episode_{episode.Identifier.Id}"
            : null;

    /// <summary>
    /// 视频发布时间（动态接口部分条目仅提供时间提示）.
    /// </summary>
    public DateTimeOffset? PublishTime => FindInnerContent<VideoInformation>()?.PublishTime
        ?? FindInnerContent<EpisodeInformation>()?.PublishTime;

    /// <summary>
    /// 是否显示强调色边框标记.
    /// </summary>
    public bool IsOpenedBorderVisible => IsLastOpened && OpenedMarkStyle == MomentVideoOpenedMarkStyle.AccentBorder;

    /// <summary>
    /// 是否显示上次打开角标.
    /// </summary>
    public bool IsOpenedBadgeVisible => IsLastOpened && OpenedMarkStyle == MomentVideoOpenedMarkStyle.Badge;

    /// <summary>
    /// 是否显示流动彩虹边框标记.
    /// </summary>
    public bool IsOpenedRainbowBorderVisible => IsLastOpened && OpenedMarkStyle == MomentVideoOpenedMarkStyle.RainbowBorder;

    /// <summary>
    /// 卡片不透明度.
    /// </summary>
    public double OpenedCardOpacity => IsLastOpened && OpenedMarkStyle == MomentVideoOpenedMarkStyle.Dimmed ? 0.65d : 1d;

    partial void OnIsLastOpenedChanged(bool value)
        => NotifyOpenedAppearanceChanged();

    partial void OnOpenedMarkStyleChanged(MomentVideoOpenedMarkStyle value)
        => NotifyOpenedAppearanceChanged();

    private void NotifyOpenedAppearanceChanged()
    {
        OnPropertyChanged(nameof(IsOpenedBorderVisible));
        OnPropertyChanged(nameof(IsOpenedBadgeVisible));
        OnPropertyChanged(nameof(IsOpenedRainbowBorderVisible));
        OnPropertyChanged(nameof(OpenedCardOpacity));
    }
}
