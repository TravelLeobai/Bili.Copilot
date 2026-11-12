// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.Models.Constants;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 视频动态详情控件.
/// </summary>
public sealed partial class VideoMomentSectionDetailControl : VideoMomentSectionDetailControlBase
{
    private bool _isActive;
    private bool _anchorUpdateQueued;
    private MomentItemViewModel? _lastAnchor;
    private ItemsRepeater? _itemsRepeater;
    private ItemsRepeater? _watchedRepeater;
    private long _paneWidthCallback;
    private bool _isApplyingPaneWidth;
    private bool _paneWidthDirty;
    private double _preferredLeftPaneRatio;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _savePaneWidthTimer;
    private bool _isPaneResizing;
    private bool _attached;
    private bool _paneResizeFrameQueued;
    private double? _pendingLeftWidth;
    private MomentSentinelPager? _pager;
    private bool _rebuildQueued;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoMomentSectionDetailControl"/> class.
    /// </summary>
    public VideoMomentSectionDetailControl()
    {
        InitializeComponent();
        _pager = new MomentSentinelPager("视频", OnPagerLoadNextBatch);
        ColumnResizer.AddHandler(PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSplitterPointerPressed), true);
        ColumnResizer.AddHandler(PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSplitterPointerFinished), true);
        ColumnResizer.AddHandler(PointerCanceledEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSplitterPointerFinished), true);
        ColumnResizer.AddHandler(PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSplitterPointerFinished), true);
        _preferredLeftPaneRatio = SettingsToolkit.ReadLocalSetting(SettingNames.MomentVideoLeftPaneRatio, -1d);
        if (!double.IsFinite(_preferredLeftPaneRatio) || _preferredLeftPaneRatio <= 0 || _preferredLeftPaneRatio >= 1)
        {
            _preferredLeftPaneRatio = double.NaN;
        }
    }

    private void OnUpersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => SelectCurrentUper();

    private void OnUperSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is MomentUperSectionViewModel item)
        {
            ViewModel?.SelectUperCommand.Execute(item);
        }
    }

    private void SelectCurrentUper()
    {
        if (!_isActive)
        {
            return;
        }

        // 不能在 Upers 的 CollectionChanged 回调里同步调 Select：
        // 那时 ItemsView 内部还没消化这次 Add，索引会越界（-1 转成 uint 直接抛
        // "indices larger than Int32.MaxValue"）。排到下一轮调度再选.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isActive || ViewModel is null)
            {
                return;
            }

            var upers = ViewModel.Upers;
            if (upers.Count == 0)
            {
                return;
            }

            var index = 0;
            if (ViewModel.SelectedAuthor is not null)
            {
                var match = upers.FirstOrDefault(p => string.Equals(p.Name, ViewModel.SelectedAuthor, StringComparison.Ordinal));
                if (match is not null)
                {
                    index = upers.IndexOf(match);
                }
            }

            if (index < 0 || index >= upers.Count)
            {
                index = 0;
            }

            UperView.Select(index);
        });
    }

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        _isActive = true;
        _paneWidthCallback = ColumnResizer.RegisterPropertyChangedCallback(
            ColumnSplitter.ColumnWidthProperty, OnPaneWidthChanged);
        _savePaneWidthTimer = DispatcherQueue.CreateTimer();
        _savePaneWidthTimer.Interval = TimeSpan.FromMilliseconds(300);
        _savePaneWidthTimer.IsRepeating = false;
        _savePaneWidthTimer.Tick += OnSavePaneWidthTick;
        ApplyPaneWidths();
        _pager?.Reset();
        _pager?.Attach(MomentSentinel);

        // 刷新重建时会把 ItemsSource 摘下来再挂回去；万一中途控件被卸载，
        // 下一次加载得把列表重新接上，否则整个网格是空的.
        if (ViewModel is not null && MomentRepeater.ItemsSource is null)
        {
            MomentRepeater.ItemsSource = ViewModel.Items;
        }

        // 动态页的两个分区控件是常驻的（切 tab 只切 Visibility），
        // 切到「视频」之前这里的 ViewModel 还是 null。以前直接解引用，
        // 第一次加载就抛空引用，把后面给顶部 UP 主头像条挂 SelectionChanged
        // 的代码全挡住了 —— 于是点头像根本没反应。
        if (ViewModel is not null)
        {
            Attach();
        }

        QueueAnchorUpdate();
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(VideoMomentSectionDetailViewModel? oldValue, VideoMomentSectionDetailViewModel? newValue)
    {
        if (!_isActive)
        {
            return;
        }

        Detach();
        if (newValue is not null)
        {
            Attach();
        }

        _pager?.Reset();
        _pager?.QueueFillCheck();
    }

    private void Attach()
    {
        var viewModel = ViewModel;
        if (viewModel is null || _attached)
        {
            return;
        }

        _attached = true;
        viewModel.ListUpdated += OnListUpdatedAsync;
        viewModel.ItemsRebuilt += OnItemsRebuilt;
        viewModel.Items.CollectionChanged += OnItemsCollectionChanged;
        viewModel.Upers.CollectionChanged += OnUpersChanged;
        MomentWatchHistoryToolkit.WatchedChanged += OnWatchedChanged;
        UperView.SelectionChanged += OnUperSelectionChanged;
        viewModel.SynchronizeWatchedItems();
        SelectCurrentUper();
        if (!viewModel.IsLoading && (viewModel.Items.Count > 0 || viewModel.WatchedItems.Count > 0))
        {
            viewModel.ResetHistoryCursor();
            viewModel.SynchronizeHistoryCommand.Execute(default);
        }
    }

    private void Detach()
    {
        var viewModel = ViewModel;
        if (!_attached || viewModel is null)
        {
            _attached = false;
            return;
        }

        viewModel.ListUpdated -= OnListUpdatedAsync;
        viewModel.ItemsRebuilt -= OnItemsRebuilt;
        viewModel.Items.CollectionChanged -= OnItemsCollectionChanged;
        viewModel.Upers.CollectionChanged -= OnUpersChanged;
        MomentWatchHistoryToolkit.WatchedChanged -= OnWatchedChanged;
        UperView.SelectionChanged -= OnUperSelectionChanged;
        _attached = false;
    }

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        Detach();
        _pager?.Detach();
        FlushPendingPaneWidth();
        _pendingLeftWidth = null;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPaneResizeFrame;
        _paneResizeFrameQueued = false;
        _isPaneResizing = false;
        View.IsLoadMorePaused = false;
        ViewModel?.SetTimelineUpdatesSuspended(false);
        _isActive = false;
        ColumnResizer.UnregisterPropertyChangedCallback(ColumnSplitter.ColumnWidthProperty, _paneWidthCallback);
        if (_savePaneWidthTimer is not null)
        {
            _savePaneWidthTimer.Stop();
            _savePaneWidthTimer.Tick -= OnSavePaneWidthTick;
            _savePaneWidthTimer = null;
        }

        SavePaneWidth();
        if (_itemsRepeater is not null)
        {
            _itemsRepeater.ElementPrepared -= OnMomentElementPrepared;
        }

        if (_watchedRepeater is not null)
        {
            _watchedRepeater.ElementPrepared -= OnMomentElementPrepared;
        }

        _itemsRepeater = null;
        _watchedRepeater = null;
    }

    private async void OnListUpdatedAsync(object? sender, EventArgs e)
    {
        if (ViewModel.Items.Count > 0)
        {
            await View.DelayCheckItemsAsync();
        }
        else
        {
            CheckWatchedLoadMore();
        }

        QueueAnchorUpdate();
        _pager?.QueueFillCheck();
        _pager?.QueueRevealIfAtBottom();
        LogRealizedCardCount();
    }

    /// <summary>
    /// 界面这一批卡片已经铺完，顺手记一下「列表里有多少条、真正建了多少张卡片」，
    /// 用来判断列表到底有没有在虚拟化.
    /// </summary>
    private void LogRealizedCardCount()
    {
        var repeater = _itemsRepeater ?? FindRepeater(MomentRepeater);
        if (repeater is null || ViewModel is null)
        {
            return;
        }

        Serilog.Log.Information(
            "动态哨兵（视频）：列表 {Total} 条，已建卡片 {Realized} 张",
            ViewModel.Items.Count,
            Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(repeater));
    }

    private void OnItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => _pager?.QueueFillCheck();

    private bool OnPagerLoadNextBatch()
    {
        if (ViewModel is null || !_isActive)
        {
            return false;
        }

        var loaded = ViewModel.LoadNextBatch();
        if (loaded)
        {
            QueueAnchorUpdate();
        }

        return loaded;
    }

    /// <summary>
    /// 刷新后重建卡片：先把已实现的卡片全部解绑、把 ItemsSource 摘下来，
    /// 让 ItemsRepeater 释放旧元素；下一轮调度再挂回新集合，卡片就按新数据从零建。
    /// 只清集合不摘 ItemsSource 的话，回收元素会留着上一条的封面/头像（刷新后串图）.
    /// </summary>
    private void OnItemsRebuilt(object? sender, EventArgs e)
    {
        if (!_isActive || _rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        _pager?.Reset();

        // 回收前先解绑，让卡片里的图片控件取消旧请求.
        if (_itemsRepeater is not null)
        {
            for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(_itemsRepeater); i++)
            {
                if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(_itemsRepeater, i)
                    is Controls.Components.MomentCardControl card)
                {
                    card.ViewModel = null;
                }
            }
        }

        MomentRepeater.ItemsSource = null;
        _lastAnchor = null;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _rebuildQueued = false;
            if (!_isActive || ViewModel is null)
            {
                return;
            }

            MomentRepeater.ItemsSource = ViewModel.Items;
            View.ResetScrollPosition();
            QueueAnchorUpdate();
            _pager?.QueueFillCheck();
            Serilog.Log.Information("动态哨兵（视频）：刷新后重建卡片 {Count} 张", ViewModel.Items.Count);
        });
    }

    private void OnWatchedChanged(object? sender, EventArgs e)
    {
        ViewModel.SynchronizeWatchedItems();
        QueueAnchorUpdate();
        if (!_isPaneResizing && ViewModel.Items.Count > 0)
        {
            _ = View.DelayCheckItemsAsync();
        }
    }

    private void OnSectionSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyPaneWidths();
        QueueAnchorUpdate();
    }

    private void ApplyPaneWidths(double? requestedLeftWidth = null)
    {
        var available = Math.Max(0, LayoutRoot.ActualWidth - 12);
        if (available <= 0)
        {
            return;
        }

        var minLeft = Math.Min(300, available * 0.55);
        var minRight = Math.Min(240, available * 0.45);
        var preferred = double.IsNaN(_preferredLeftPaneRatio)
            ? available - Math.Clamp(available * 0.28, 220, 360)
            : available * _preferredLeftPaneRatio;
        var maxLeft = Math.Max(minLeft, available - minRight);
        var leftWidth = Math.Clamp(requestedLeftWidth ?? preferred, minLeft, maxLeft);
        _isApplyingPaneWidth = true;
        try
        {
            if (ColumnResizer.MinColumnWidth != minLeft)
            {
                ColumnResizer.MinColumnWidth = minLeft;
            }

            if (ColumnResizer.MaxColumnWidth != maxLeft)
            {
                ColumnResizer.MaxColumnWidth = maxLeft;
            }

            if (Math.Abs(ColumnResizer.ColumnWidth - leftWidth) > 0.01)
            {
                ColumnResizer.ColumnWidth = leftWidth;
            }

            var rightWidth = Math.Round(available - leftWidth);
            if (Math.Abs(WatchedColumn.Width.Value - rightWidth) >= 1)
            {
                WatchedColumn.Width = new GridLength(rightWidth);
            }
        }
        finally
        {
            _isApplyingPaneWidth = false;
        }

        QueueAnchorUpdate();
    }

    private void OnPaneWidthChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_isApplyingPaneWidth || !_isActive || LayoutRoot.ActualWidth <= 12)
        {
            return;
        }

        _pendingLeftWidth = ColumnResizer.ColumnWidth;
        if (!_paneResizeFrameQueued)
        {
            _paneResizeFrameQueued = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnPaneResizeFrame;
        }
    }

    private void OnPaneResizeFrame(object? sender, object args)
    {
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPaneResizeFrame;
        _paneResizeFrameQueued = false;
        if (_isActive)
        {
            FlushPendingPaneWidth();
        }
    }

    private void FlushPendingPaneWidth()
    {
        if (_pendingLeftWidth is not { } width || LayoutRoot.ActualWidth <= 12)
        {
            return;
        }

        _pendingLeftWidth = null;
        ApplyPaneWidths(width);
        _preferredLeftPaneRatio = ColumnResizer.ColumnWidth / (LayoutRoot.ActualWidth - 12);
        _paneWidthDirty = true;
        _savePaneWidthTimer?.Stop();
        _savePaneWidthTimer?.Start();
    }

    private void OnSplitterPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (!_isActive || !args.GetCurrentPoint(ColumnResizer).IsInContact)
        {
            return;
        }

        _isPaneResizing = true;
        View.IsLoadMorePaused = true;
        ViewModel.SetTimelineUpdatesSuspended(true);
        ClearItemAnimations(_itemsRepeater);
        ClearItemAnimations(_watchedRepeater);
    }

    private void OnSplitterPointerFinished(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (!_isPaneResizing)
        {
            return;
        }

        FlushPendingPaneWidth();
        _isPaneResizing = false;
        View.IsLoadMorePaused = false;
        ViewModel.SetTimelineUpdatesSuspended(false);
        QueueAnchorUpdate();
        _ = View.DelayCheckItemsAsync();
        CheckWatchedLoadMore();
    }

    private void OnSavePaneWidthTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        => SavePaneWidth();

    private void SavePaneWidth()
    {
        if (_paneWidthDirty)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.MomentVideoLeftPaneRatio, _preferredLeftPaneRatio);
            _paneWidthDirty = false;
        }
    }

    private void OnSplitterDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        _pendingLeftWidth = null;
        _preferredLeftPaneRatio = double.NaN;
        _paneWidthDirty = false;
        _savePaneWidthTimer?.Stop();
        SettingsToolkit.DeleteLocalSetting(SettingNames.MomentVideoLeftPaneRatio);
        ApplyPaneWidths();
        e.Handled = true;
    }

    private void OnSplitterKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)
        {
            ColumnResizer.ColumnWidth += e.Key == Windows.System.VirtualKey.Left ? -16 : 16;
            e.Handled = true;
        }
    }

    private void OnScrollPositionChanged(object? sender, EventArgs e) => QueueAnchorUpdate();

    private void OnWatchedViewChanged(ScrollView sender, object e) => CheckWatchedLoadMore();

    private void CheckWatchedLoadMore()
    {
        if (!_isActive || _isPaneResizing)
        {
            return;
        }

        var remaining = WatchedView.ExtentHeight - WatchedView.ViewportHeight - WatchedView.VerticalOffset;
        if (remaining > 200)
        {
            return;
        }

        // 先补"已经拿到、但还没铺到界面"的那一批：哨兵没挂上时这条兜底仍然能工作.
        if (ViewModel.HasHiddenItems)
        {
            ViewModel.LoadNextBatch();
            return;
        }

        // 这一批已经全部铺完，再去拉下一页数据（LoadUnwatchedItems 内部会判断有没有更多）.
        if (!ViewModel.IsHistoryLoading && !ViewModel.IsLoading)
        {
            ViewModel.LoadUnwatchedItemsCommand.Execute(default);
        }
    }

    private void OnMomentLayoutUpdated(object? sender, object e) => QueueAnchorUpdate();

    private void QueueAnchorUpdate()
    {
        if (!_isActive || _isPaneResizing || _anchorUpdateQueued)
        {
            return;
        }

        _anchorUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _anchorUpdateQueued = false;
            if (_isActive && !_isPaneResizing)
            {
                UpdateTimelineAnchor();
            }
        });
    }

    private void UpdateTimelineAnchor()
    {
        if (_itemsRepeater is null)
        {
            _itemsRepeater = FindRepeater(MomentRepeater);
            ConfigureItemAnimations(_itemsRepeater);
        }

        if (_watchedRepeater is null)
        {
            _watchedRepeater = FindRepeater(WatchedRepeater);
            ConfigureItemAnimations(_watchedRepeater);
        }

        if (_itemsRepeater is null)
        {
            return;
        }

        MomentItemViewModel? anchor = null;
        var top = double.MaxValue;
        var right = double.MinValue;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(_itemsRepeater); i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(_itemsRepeater, i) is not FrameworkElement element
                || element.ActualHeight <= 0)
            {
                continue;
            }

            var index = _itemsRepeater.GetElementIndex(element);
            if (index < 0 || index >= ViewModel.Items.Count)
            {
                continue;
            }

            var point = element.TransformToVisual(View).TransformPoint(new Windows.Foundation.Point());
            if (point.Y + element.ActualHeight <= 0 || point.Y >= View.ActualHeight)
            {
                continue;
            }

            if (point.Y < top - 1 || (Math.Abs(point.Y - top) <= 1 && point.X > right))
            {
                top = point.Y;
                right = point.X;
                anchor = ViewModel.Items[index];
            }
        }

        ViewModel.UpdateTimelineAnchor(anchor, View.GetScrollPosition() < 1);
        if (!ReferenceEquals(anchor, _lastAnchor))
        {
            _lastAnchor = anchor;
            WatchedView.ScrollTo(0, 0);
        }
    }

    private void ConfigureItemAnimations(ItemsRepeater? repeater)
    {
        if (repeater is null)
        {
            return;
        }

        repeater.ElementPrepared += OnMomentElementPrepared;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(repeater); i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(repeater, i) is UIElement element)
            {
                SetItemTransitions(element);
            }
        }
    }

    private void OnMomentElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        => SetItemTransitions(args.Element);

    private void SetItemTransitions(UIElement element)
    {
        if (_isPaneResizing)
        {
            element.Transitions = null;
            return;
        }

        element.Transitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
        {
            new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition
            {
                FromHorizontalOffset = 0,
                FromVerticalOffset = 12,
                IsStaggeringEnabled = false,
            },
        };
    }

    private static void ClearItemAnimations(ItemsRepeater? repeater)
    {
        if (repeater is null)
        {
            return;
        }

        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(repeater); i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(repeater, i) is UIElement element)
            {
                element.Transitions = null;
            }
        }
    }

    private static ItemsRepeater? FindRepeater(DependencyObject parent)
    {
        if (parent is ItemsRepeater repeater)
        {
            return repeater;
        }

        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindRepeater(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i)) is { } child)
            {
                return child;
            }
        }

        return null;
    }
}

/// <summary>
/// 视频动态详情控件基类.
/// </summary>
public abstract class VideoMomentSectionDetailControlBase : LayoutUserControlBase<VideoMomentSectionDetailViewModel>
{
}


