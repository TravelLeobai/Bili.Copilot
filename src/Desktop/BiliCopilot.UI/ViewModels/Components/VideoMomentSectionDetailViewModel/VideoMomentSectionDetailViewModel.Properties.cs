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
}
