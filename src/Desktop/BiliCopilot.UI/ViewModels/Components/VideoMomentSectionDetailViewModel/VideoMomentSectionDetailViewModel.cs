// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.ViewModels.View;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频动态分区详情视图模型.
/// </summary>
public sealed partial class VideoMomentSectionDetailViewModel : ViewModelBase, IMomentSectionDetailViewModel
{
    /// <summary>
    /// 每次往界面补多少张卡片（这个网格是两到三列，12 张差不多一屏）.
    /// </summary>
    private const int PageBatchSize = 12;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoMomentSectionDetailViewModel"/> class.
    /// </summary>
    public VideoMomentSectionDetailViewModel(
        IMomentDiscoveryService service)
    {
        _service = service;
        _logger = this.Get<ILogger<VideoMomentSectionDetailViewModel>>();
        _lastOpenedMomentId = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.LastOpenedMomentVideoId, string.Empty);
        _openedMarkStyle = SettingsToolkit.ReadLocalSetting(
            Models.Constants.SettingNames.MomentVideoOpenedMarkStyle,
            Models.Constants.MomentVideoOpenedMarkStyle.AccentBorder);
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (_allItems.Count > 0 || _preventLoadMore)
        {
            return;
        }

        await LoadItemsAsync();
        await LoadUpersAsync();
        SaveLastTenMomentIds();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (IsLoading || IsHistoryLoading || _areTimelineUpdatesSuspended)
        {
            return;
        }

        _offset = default;
        _baseline = default;
        IsEmpty = false;
        _preventLoadMore = false;
        _rebuildPending = true;
        _visibleCount = PageBatchSize;
        _unwatched.Clear();
        Items.Clear();
        _allItems.Clear();
        _watchedTimeline.Clear();
        _momentIds.Clear();
        WatchedItems.Clear();
        IsWatchedEmpty = true;
        _timelineAnchor = null;
        _isAtTop = true;
        TimelineTip = null;
        _historyOffset = 0;
        _historyExhausted = false;
        _historyScannedThrough = DateTimeOffset.MaxValue;
        this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await LoadItemsAsync();
            SaveLastTenMomentIds();
        });
    }

    [RelayCommand]
    private async Task LoadUnwatchedItemsAsync()
    {
        // 还有没铺出来的卡片时先别去拉新数据：列表只有一屏高，「到底了」的判定会一直成立，
        // 后台就会一页接一页把整条动态拉干净；等用户滚到底把这一批补完再去拿下一页.
        if (HasHiddenItems)
        {
            return;
        }

        // 左侧空了以后由右侧滚动触发加载，避免空列表自动拉完整条动态.
        if (Items.Count > 0 || _allItems.Count == 0)
        {
            await LoadItemsCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task LoadItemsAsync()
    {
        if (IsEmpty || IsLoading || IsHistoryLoading || _preventLoadMore || _areTimelineUpdatesSuspended)
        {
            return;
        }

        try
        {
            IsLoading = true;
            var view = await _service.GetVideoMomentsAsync(_offset, _baseline);
            if (view is not null)
            {
                _offset = view.Offset;
                _baseline = view.UpdateBaseline;
                _preventLoadMore = view.HasMoreMoments != true;
                foreach (var item in view.Moments.Select(p => CreateItem(p)))
                {
                    if (_momentIds.Add(item.Data.Id))
                    {
                        _allItems.Add(item);
                    }
                }

                SynchronizeWatchedItems();
            }
            else
            {
                _preventLoadMore = true;
            }
        }
        catch (Exception ex)
        {
            _preventLoadMore = true;
            _logger.LogError(ex, "加载动态列表失败.");
        }
        finally
        {
            IsEmpty = _allItems.Count == 0;
            IsLoading = false;
        }

        await SynchronizeHistoryAsync();
        ListUpdated?.Invoke(this, EventArgs.Empty);

        if (_rebuildPending)
        {
            // 刷新后整批重来：通知界面把卡片元素从零重建，避免回收元素留着上一条的封面/头像.
            _rebuildPending = false;
            ItemsRebuilt?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowComment(MomentItemViewModel item)
        => this.Get<MomentPageViewModel>().ShowCommentCommand.Execute(item.Data);

    /// <summary>
    /// 切换头像条选中的 UP 主，null 或全部动态表示不过滤.
    /// </summary>
    [RelayCommand]
    private void SelectUper(MomentUperSectionViewModel? item)
        => SelectedAuthor = item is null || item.IsTotal ? null : item.Name;

    partial void OnSelectedAuthorChanged(string? value)
        => SynchronizeWatchedItems();

    private async Task LoadUpersAsync()
    {
        if (Upers.Count > 0)
        {
            return;
        }

        try
        {
            Upers.Add(new MomentUperSectionViewModel(default));
            var response = await _service.GetComprehensiveMomentsAsync();
            if (response?.Users is not null)
            {
                foreach (var user in response.Users)
                {
                    Upers.Add(new MomentUperSectionViewModel(user));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载关注 UP 主头像列表失败.");
        }
    }

    /// <summary>
    /// 更新最近打开标记的显示样式.
    /// </summary>
    public void SetOpenedMarkStyle(Models.Constants.MomentVideoOpenedMarkStyle style)
    {
        _openedMarkStyle = style;
        foreach (var item in _allItems)
        {
            item.OpenedMarkStyle = style;
        }
    }

    private MomentItemViewModel CreateItem(Richasy.BiliKernel.Models.Moment.MomentInformation data)
    {
        return new MomentItemViewModel(data, Models.Constants.MomentCardStyle.Video, ShowComment, MarkOpened)
        {
            IsLastOpened = data.Id == _lastOpenedMomentId,
            OpenedMarkStyle = _openedMarkStyle,
        };
    }

    private void MarkOpened(MomentItemViewModel openedItem)
    {
        _lastOpenedMomentId = openedItem.Data.Id;
        SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastOpenedMomentVideoId, _lastOpenedMomentId);
        foreach (var item in _allItems)
        {
            item.IsLastOpened = ReferenceEquals(item, openedItem);
        }
    }

    private void SaveLastTenMomentIds()
    {
        var momentIds = _allItems.Take(10).Select(p => p.Data.Id).ToList();
        var lastTenMomentIds = string.Join(',', momentIds);
        SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastTenMomentIds, lastTenMomentIds);
    }

    /// <summary>
    /// 按本地和云端观看记录重新划分动态，两侧共用同一条时间线.
    /// </summary>
    public void SynchronizeWatchedItems()
    {
        if (_areTimelineUpdatesSuspended)
        {
            _hasDeferredWatchedUpdate = true;
            return;
        }

        _unwatched.Clear();
        _watchedTimeline.Clear();
        for (var i = 0; i < _allItems.Count; i++)
        {
            var item = _allItems[i];
            if (SelectedAuthor is not null && !string.Equals(item.Author, SelectedAuthor, StringComparison.Ordinal))
            {
                continue;
            }

            if (IsWatched(item))
            {
                _watchedTimeline.Add((item, i));
            }
            else
            {
                _unwatched.Add(item);
            }
        }

        ApplyVisibleWindow();
        IsUnwatchedEmpty = _allItems.Count > 0 && _unwatched.Count == 0;
        UpdateWatchedTimeline();
    }

    /// <summary>
    /// 已经加载、但还没铺到界面上的条目（等用户滚到底再补）.
    /// </summary>
    public bool HasHiddenItems => Items.Count < _unwatched.Count;

    /// <summary>
    /// 再往界面补一批卡片（滚动到底时由界面调用）；已经没有隐藏的条目就返回 <c>false</c>.
    /// </summary>
    /// <returns>是否真的补进来了新卡片.</returns>
    public bool LoadNextBatch()
    {
        if (Items.Count >= _unwatched.Count)
        {
            return false;
        }

        _visibleCount = Math.Min(_unwatched.Count, _visibleCount + PageBatchSize);
        ApplyVisibleWindow();
        Serilog.Log.Information("动态哨兵（视频）：补到 {Shown}/{Total}", Items.Count, _unwatched.Count);
        return true;
    }

    /// <summary>
    /// 把已经加载的动态里「未看过」的那一段按当前窗口大小铺到界面上。
    /// 只铺窗口内的条目，其余等用户滚到底再补，避免一次把整条动态都建成卡片.
    /// </summary>
    private void ApplyVisibleWindow()
        => UpdateCollection(Items, _unwatched.Take(Math.Max(_visibleCount, 0)).ToList());

    /// <summary>
    /// 跟随左侧顶部一行最右边的视频定位已看时间线.
    /// </summary>
    public void UpdateTimelineAnchor(MomentItemViewModel? anchor, bool isAtTop)
    {
        if (_areTimelineUpdatesSuspended || (ReferenceEquals(anchor, _timelineAnchor) && isAtTop == _isAtTop))
        {
            return;
        }

        _timelineAnchor = anchor;
        _isAtTop = isAtTop;
        TimelineTip = anchor?.Tip;
        UpdateWatchedTimeline();
    }

    /// <summary>
    /// 拖动分栏时延后集合更新，结束后只补一次同步.
    /// </summary>
    public void SetTimelineUpdatesSuspended(bool suspended)
    {
        _areTimelineUpdatesSuspended = suspended;
        if (!suspended && _hasDeferredWatchedUpdate)
        {
            _hasDeferredWatchedUpdate = false;
            SynchronizeWatchedItems();
        }
    }

    /// <summary>
    /// 重新进入页面时从最新记录同步，避免漏掉其他设备新增的历史分页.
    /// </summary>
    public void ResetHistoryCursor()
    {
        if (!IsHistoryLoading)
        {
            _historyOffset = 0;
            _historyExhausted = false;
            _historyScannedThrough = DateTimeOffset.MaxValue;
        }
    }

    /// <summary>
    /// 回到动态页时重新检查其他设备的最近观看记录.
    /// </summary>
    [RelayCommand]
    private async Task SynchronizeHistoryAsync()
    {
        if (IsHistoryLoading)
        {
            return;
        }

        IsHistoryLoading = true;
        HasHistoryError = false;
        try
        {
            // 历史记录按观看时间分页。观看必定晚于发布，因此只需扫描到
            // 当前已加载动态中最早的发布时间；缺少时间时保守地扫描到末尾.
            var cutoff = _allItems.Select(p => MomentTimelineToolkit.GetPublishLowerBound(p.PublishTime, p.Tip))
                .DefaultIfEmpty(DateTimeOffset.MinValue).Min();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var service = this.Get<IViewHistoryService>();
            var offset = 0L;
            var isRecentPage = true;
            while (isRecentPage || (!_historyExhausted && _historyScannedThrough >= cutoff))
            {
                var group = await service.GetViewHistoryAsync(ViewHistoryTabType.Video, offset, timeout.Token);
                foreach (var video in group?.Videos ?? Enumerable.Empty<VideoInformation>())
                {
                    var progress = video.GetExtensionIfNotNull<int?>(VideoExtensionDataId.Progress);
                    if (MomentTimelineToolkit.IsWatchedProgress(progress, video.Duration))
                    {
                        MomentWatchHistoryToolkit.MarkWatched($"video_{video.Identifier.Id}", false);
                    }
                }

                var next = group?.Offset ?? 0;
                if (!isRecentPage || (_historyOffset == 0 && !_historyExhausted))
                {
                    _historyExhausted = next == 0 || next == offset;
                    _historyOffset = next;
                    // Offset 是服务游标，只使用记录中的观看日期判断扫描范围.
                    _historyScannedThrough = (group?.Videos ?? Enumerable.Empty<VideoInformation>())
                        .Select(p => p.GetExtensionIfNotNull<DateTimeOffset?>(VideoExtensionDataId.CollectTime)
                            ?? DateTimeOffset.MaxValue)
                        .DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                }

                isRecentPage = false;
                offset = _historyOffset;
                SynchronizeWatchedItems();
            }
        }
        catch (Exception ex)
        {
            HasHistoryError = true;
            _logger.LogWarning(ex, "同步动态已看记录失败，将保留已同步和本地的观看状态.");
        }
        finally
        {
            IsHistoryLoading = false;
            ListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsWatched(MomentItemViewModel item)
        => item.WatchHistoryKey is { } key && MomentWatchHistoryToolkit.IsWatched(key);

    private void UpdateWatchedTimeline()
    {
        var anchorIndex = _timelineAnchor is null ? 0 : _allItems.IndexOf(_timelineAnchor);
        var watched = _watchedTimeline.Where(entry => MomentTimelineToolkit.IsInWatchedWindow(
                entry.Item.PublishTime, entry.Index, _timelineAnchor?.PublishTime, anchorIndex, _isAtTop))
            .Select(entry => entry.Item).ToList();
        UpdateCollection(WatchedItems, watched);
        IsWatchedEmpty = WatchedItems.Count == 0;
        RequestInterestAnalysis();
    }

    private static void UpdateCollection(
        System.Collections.ObjectModel.ObservableCollection<MomentItemViewModel> target,
        List<MomentItemViewModel> source)
    {
        if (target.Count == source.Count && target.SequenceEqual(source))
        {
            return;
        }

        var included = source.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!included.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], source[i]))
            {
                continue;
            }

            var oldIndex = target.IndexOf(source[i]);
            if (oldIndex >= 0)
            {
                target.Move(oldIndex, i);
            }
            else
            {
                target.Insert(i, source[i]);
            }
        }
    }

    /// <summary>
    /// 触发一次观看兴趣分析（去抖 + 结果签名判重）.
    /// </summary>
    private void RequestInterestAnalysis()
    {
        if (!AiInterestAnalyzer.IsEnabled || !AiInterestAnalyzer.IsConfigured)
        {
            return;
        }

        var samples = WatchedItems
            .Take(12)
            .Select(p => new InterestSample(
                p.VideoTitle ?? string.Empty,
                p.Author ?? string.Empty,
                p.Description?.Text ?? string.Empty,
                p.VideoCover?.ToString() ?? string.Empty))
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .ToList();

        if (samples.Count == 0)
        {
            WatchedInterestText = null;
            IsInterestAnalyzing = false;
            return;
        }

        var signature = string.Join('|', samples.Select(p => p.Title));
        if (signature == _interestSignature)
        {
            return;
        }

        _interestSignature = signature;
        _interestCts?.Cancel();
        _interestCts = new CancellationTokenSource();
        var token = _interestCts.Token;

        IsInterestAnalyzing = true;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // 用户滚动很快时只在停下来的那一版上跑，省请求.
                    await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                    var text = await AiInterestAnalyzer.AnalyzeAsync(samples, _logger).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        WatchedInterestText = text;
                        OnPropertyChanged(nameof(HasWatchedInterest));
                        IsInterestAnalyzing = false;
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "分析观看兴趣失败");
                    this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, () => IsInterestAnalyzing = false);
                }
            },
            token);
    }
}
