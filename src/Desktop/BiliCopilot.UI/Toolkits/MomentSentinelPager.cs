// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.Toolkits;

/// <summary>
/// 动态列表底部哨兵的「滚到底再补一批」触发器。
/// 哨兵是列表内容末尾的 1px 透明 Border，挂载时由这里找到外层滚动宿主并订阅 ViewChanged；
/// 之后只有同时满足「离尾部 8px 以内」和「比上次触发又往下滚了 24px 以上」才向列表要下一批。
/// 这两个条件缺一不可：只看是否到底的话，内容还没撑满一屏时事件会反复触发，
/// 一口气把整个列表都铺出来（合集面板踩过这个坑，见 a3529a9）。
/// </summary>
internal sealed class MomentSentinelPager
{
    /// <summary>离尾部多少像素算「滚到底」.</summary>
    private const double NearBottomThreshold = 8d;

    /// <summary>比上次触发又往下滚多少像素，才允许再补一批.</summary>
    private const double TriggerAdvanceThreshold = 24d;

    /// <summary>一次数据变化最多连着补几批，防止内容永远撑不满时无限补.</summary>
    private const int MaxFillBatches = 8;

    private readonly string _sectionName;
    private readonly Func<bool> _loadNextBatch;

    private ScrollView? _scrollView;
    private ScrollViewer? _scrollViewer;
    private DependencyObject? _sentinel;
    private bool _loggedAttach;
    private double _lastTriggerOffset = -1d;
    private bool _fillCheckQueued;
    private bool _revealCheckQueued;
    private int _fillBatchCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="MomentSentinelPager"/> class.
    /// </summary>
    /// <param name="sectionName">分区名，只用于日志.</param>
    /// <param name="loadNextBatch">向列表要下一批；返回 <c>false</c> 表示没有更多隐藏项.</param>
    public MomentSentinelPager(string sectionName, Func<bool> loadNextBatch)
    {
        _sectionName = sectionName;
        _loadNextBatch = loadNextBatch;
    }

    /// <summary>是否已经挂上滚动宿主.</summary>
    public bool IsAttached => _scrollView is not null || _scrollViewer is not null;

    /// <summary>
    /// 把哨兵挂到外层滚动宿主上；找不到宿主就什么也不做（等下次挂载再试）.
    /// </summary>
    /// <param name="sentinel">列表末尾的哨兵元素.</param>
    public void Attach(DependencyObject? sentinel)
    {
        Detach();
        _sentinel = sentinel;
        if (sentinel is null)
        {
            return;
        }

        _lastTriggerOffset = -1d;
        _fillBatchCount = 0;
        BindHost(sentinel);

        // 控件刚加载时哨兵可能还没进可视树（祖先链不全），等它自己 Loaded 再挂一次.
        if (sentinel is FrameworkElement element)
        {
            element.Loaded -= OnSentinelLoaded;
            element.Loaded += OnSentinelLoaded;
        }

        if (!IsAttached)
        {
            // 再排一轮兜底：控件加载完之后本轮可视树就该齐了.
            sentinel.DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, EnsureAttached);
        }

        QueueFillCheck();
    }

    /// <summary>
    /// 还没挂上滚动宿主时再试一次（哨兵刚加载、祖先链那时可能还不全）.
    /// </summary>
    public void EnsureAttached()
    {
        if (IsAttached || _sentinel is null)
        {
            return;
        }

        BindHost(_sentinel);
        if (IsAttached)
        {
            QueueFillCheck();
        }
    }

    private void BindHost(DependencyObject sentinel)
    {
        if (FindAncestor<ScrollView>(sentinel) is { } scrollView)
        {
            _scrollView = scrollView;
            scrollView.ViewChanged += OnScrollViewChanged;
        }
        else if (FindAncestor<ScrollViewer>(sentinel) is { } scrollViewer)
        {
            _scrollViewer = scrollViewer;
            scrollViewer.ViewChanged += OnScrollViewerViewChanged;
        }
        else
        {
            return;
        }

        if (_loggedAttach)
        {
            return;
        }

        _loggedAttach = true;
        Serilog.Log.Information("动态哨兵（{Section}）：挂上滚动宿主", _sectionName);
    }

    private void OnSentinelLoaded(object sender, RoutedEventArgs e) => EnsureAttached();

    /// <summary>解绑滚动宿主.</summary>
    public void Detach()
    {
        if (_scrollView is not null)
        {
            _scrollView.ViewChanged -= OnScrollViewChanged;
            _scrollView = null;
        }

        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
            _scrollViewer = null;
        }

        if (_sentinel is FrameworkElement element)
        {
            element.Loaded -= OnSentinelLoaded;
        }

        _fillCheckQueued = false;
    }

    /// <summary>重置触发位置（刷新、换列表时用）.</summary>
    public void Reset()
    {
        _lastTriggerOffset = -1d;
        _fillBatchCount = 0;
    }

    /// <summary>
    /// 数据变化后排队检查一次「内容够不够撑满可视区」，不够就继续补，补到撑满就停。
    /// 用排队而不是直接判断，是因为集合刚变时布局还没跑完，ExtentHeight 还是旧值.
    /// </summary>
    public void QueueFillCheck()
    {
        EnsureAttached();
        if (!IsAttached || _fillCheckQueued)
        {
            return;
        }

        var queue = _scrollView?.DispatcherQueue ?? _scrollViewer?.DispatcherQueue;
        if (queue is null)
        {
            return;
        }

        _fillCheckQueued = true;
        queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _fillCheckQueued = false;
            FillNextBatch();
        });
    }

    /// <summary>
    /// 新数据到了：用户如果正停在底部，就顺手放一批出来，
    /// 免得他停在原地看不到新内容、还得先滚一下才动.
    /// </summary>
    public void QueueRevealIfAtBottom()
    {
        EnsureAttached();
        if (!IsAttached || _revealCheckQueued)
        {
            return;
        }

        var queue = _scrollView?.DispatcherQueue ?? _scrollViewer?.DispatcherQueue;
        if (queue is null)
        {
            return;
        }

        _revealCheckQueued = true;
        queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _revealCheckQueued = false;
            if (!IsAttached || GetRemainingToBottom() > NearBottomThreshold)
            {
                return;
            }

            if (_loadNextBatch())
            {
                // 记成一次「已经触发过的位置」，同一位置不重复补.
                _lastTriggerOffset = _scrollView?.VerticalOffset ?? _scrollViewer?.VerticalOffset ?? 0d;
            }
        });
    }

    private void FillNextBatch()
    {
        if (!IsAttached || _fillBatchCount >= MaxFillBatches)
        {
            return;
        }

        // 内容已经能滚动（超过一屏）就停手，剩下的等用户自己往下滚.
        if (GetRemainingToBottom() > NearBottomThreshold)
        {
            _fillBatchCount = 0;
            return;
        }

        _fillBatchCount++;
        if (!_loadNextBatch())
        {
            _fillBatchCount = 0;
            return;
        }

        QueueFillCheck();
    }

    private void OnScrollViewChanged(ScrollView sender, object args)
        => TryLoadNextBatch(sender.VerticalOffset);

    private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => TryLoadNextBatch(_scrollViewer?.VerticalOffset ?? 0d);

    private void TryLoadNextBatch(double offset)
    {
        if (GetRemainingToBottom() > NearBottomThreshold)
        {
            return;
        }

        if (_lastTriggerOffset >= 0 && Math.Abs(offset - _lastTriggerOffset) < TriggerAdvanceThreshold)
        {
            return;
        }

        _lastTriggerOffset = offset;
        Serilog.Log.Information("动态哨兵（{Section}）：滚到底，向列表要下一批", _sectionName);
        _ = _loadNextBatch();
    }

    private double GetRemainingToBottom()
        => _scrollView is not null
            ? _scrollView.ExtentHeight - _scrollView.ViewportHeight - _scrollView.VerticalOffset
            : (_scrollViewer?.ExtentHeight ?? 0d) - (_scrollViewer?.ViewportHeight ?? 0d) - (_scrollViewer?.VerticalOffset ?? 0d);

    /// <summary>
    /// 往上找指定类型的祖先元素.
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T target)
            {
                return target;
            }

            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }

        return null;
    }
}
