// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频动态分区详情视图模型.
/// </summary>
public sealed partial class VideoMomentSectionDetailViewModel
{
    private readonly IMomentDiscoveryService _service;
    private readonly ILogger<VideoMomentSectionDetailViewModel> _logger;

    private bool _preventLoadMore;
    private string? _offset;
    private string? _baseline;
    private string? _lastOpenedMomentId;
    private MomentVideoOpenedMarkStyle _openedMarkStyle;
    private readonly List<MomentItemViewModel> _allItems = new();
    private readonly List<(MomentItemViewModel Item, int Index)> _watchedTimeline = new();
    private readonly HashSet<string> _momentIds = new();
    private long _historyOffset;
    private bool _historyExhausted;
    private DateTimeOffset _historyScannedThrough = DateTimeOffset.MaxValue;
    private MomentItemViewModel? _timelineAnchor;
    private bool _isAtTop = true;
    private bool _areTimelineUpdatesSuspended;
    private bool _hasDeferredWatchedUpdate;
    private string? _interestSignature;
    private CancellationTokenSource? _interestCts;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isHistoryLoading;

    [ObservableProperty]
    private bool _isWatchedEmpty = true;

    [ObservableProperty]
    private bool _isUnwatchedEmpty;

    [ObservableProperty]
    private string? _timelineTip;

    [ObservableProperty]
    private bool _hasHistoryError;

    /// <summary>
    /// 当前按 UP 主过滤的作者名，为空表示全部动态.
    /// </summary>
    [ObservableProperty]
    private string? _selectedAuthor;

    /// <summary>
    /// 关注 UP 主头像条，第一条为全部动态.
    /// </summary>
    public ObservableCollection<MomentUperSectionViewModel> Upers { get; } = new();

    /// <summary>
    /// 列表已完成更新.
    /// </summary>
    public event EventHandler ListUpdated;

    /// <inheritdoc/>
    public MomentSectionType SectionType => MomentSectionType.Video;

    /// <summary>
    /// 条目列表.
    /// </summary>
    public ObservableCollection<MomentItemViewModel> Items { get; } = new();

    /// <summary>
    /// 跟随左侧当前时间位置显示的已看视频.
    /// </summary>
    public ObservableCollection<MomentItemViewModel> WatchedItems { get; } = new();

    /// <summary>
    /// 已看过视频的观看兴趣分析（由大模型生成）.
    /// </summary>
    [ObservableProperty]
    private string _watchedInterestText;

    /// <summary>
    /// 是否正在分析观看兴趣.
    /// </summary>
    [ObservableProperty]
    private bool _isInterestAnalyzing;

    /// <summary>
    /// 是否已有兴趣分析结果.
    /// </summary>
    public bool HasWatchedInterest => !string.IsNullOrWhiteSpace(WatchedInterestText);
}
