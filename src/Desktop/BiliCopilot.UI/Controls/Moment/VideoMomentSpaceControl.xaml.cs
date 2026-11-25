// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.Services.Media;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.BiliKernel.Models.Media;
using Windows.Foundation;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 视频动态空间控件。
/// <para>
/// 同时充当「视口哨兵」：滚动 / 尺寸变化时把当前卡片按可见面积、悬停、焦点、停留时间算成候选，
/// 交给 <see cref="MediaPrefetchScheduler"/> 去做主动预取。哨兵只负责「谁在视口里」，
/// 预取策略（优先级、防抖、预算、保留期）全在调度器里，方便单测。
/// </para>
/// </summary>
public sealed partial class VideoMomentSpaceControl : UserMomentDetailControlBase
{
    private readonly string _prefetchOwnerKey = "moment-video:" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<MomentCardControl, CardInteraction> _interactions = [];
    private readonly Dictionary<string, long> _visibleSince = new(StringComparer.Ordinal);
    private bool _prefetchQueued;
    private MediaPrefetchScheduler? _scheduler;
    private ILogger<VideoMomentSpaceControl>? _logger;
    private bool _prefetchSkippedLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoMomentSpaceControl"/> class.
    /// </summary>
    public VideoMomentSpaceControl() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        ViewModel.ListUpdated += OnListUpdatedAsync;
        _scheduler = this.Get<MediaPrefetchScheduler>();
        _logger = this.Get<ILogger<VideoMomentSpaceControl>>();
        if (View.ScrollHost is { } host)
        {
            host.ViewChanged += OnViewportChanged;
            host.SizeChanged += OnViewportSizeChanged;
        }

        QueuePrefetchEvaluation();
    }

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        ViewModel.ListUpdated -= OnListUpdatedAsync;
        if (View.ScrollHost is { } host)
        {
            host.ViewChanged -= OnViewportChanged;
            host.SizeChanged -= OnViewportSizeChanged;
        }

        foreach (var pair in _interactions)
        {
            DetachInteraction(pair.Key);
        }

        _interactions.Clear();
        _visibleSince.Clear();
        _scheduler?.DetachViewport(_prefetchOwnerKey);
        _scheduler = null;
    }

    private async void OnListUpdatedAsync(object? sender, EventArgs e)
    {
        await View.DelayCheckItemsAsync();
        QueuePrefetchEvaluation();
    }

    private void OnViewportChanged(ScrollView sender, object e)
        => QueuePrefetchEvaluation();

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
        => QueuePrefetchEvaluation();

    /// <summary>
    /// 把「算候选」挂到 DispatcherQueue 低优先级：一次滚动会来很多次 ViewChanged，
    /// 但只需要在布局稳定后扫一遍；真正的 500ms 防抖在调度器的 Gate 里.
    /// </summary>
    private void QueuePrefetchEvaluation()
    {
        if (_prefetchQueued || _scheduler is null)
        {
            return;
        }

        _prefetchQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _prefetchQueued = false;
            SubmitCandidates();
        });
    }

    private void SubmitCandidates()
    {
        var scheduler = _scheduler;
        if (scheduler is null || View.ScrollHost is not { } host || !IsLoaded)
        {
            return;
        }

        try
        {
            if (!scheduler.IsEnabled)
            {
                if (!_prefetchSkippedLogged)
                {
                    _prefetchSkippedLogged = true;
                    _logger?.LogDebug("预取：未启用，视口哨兵空转");
                }

                return;
            }

            var cards = new List<MomentCardControl>();
            CollectCards(this, cards);
            var viewportWidth = host.ViewportWidth;
            var viewportHeight = host.ViewportHeight;
            if (viewportHeight <= 0 || viewportWidth <= 0)
            {
                return;
            }

            var now = Environment.TickCount64;
            var seen = new HashSet<MomentCardControl>();
            var candidates = new List<MediaPrefetchCandidate>(cards.Count);
            var order = 0;
            foreach (var card in cards)
            {
                var viewModel = card.ViewModel;
                if (viewModel is null || !TryGetMedia(viewModel, out var videoId, out var title, out var isPgc))
                {
                    continue;
                }

                seen.Add(card);
                var interaction = EnsureInteraction(card);
                var bounds = GetBoundsInHost(host, card);
                var ratio = GetVisibleRatio(bounds, viewportWidth, viewportHeight);
                var isNextScreen = false;
                if (ratio <= 0 && bounds is { } rect && rect.Top >= viewportHeight)
                {
                    isNextScreen = rect.Top <= viewportHeight * 2;
                }

                if (ratio > 0)
                {
                    if (!_visibleSince.ContainsKey(videoId))
                    {
                        _visibleSince[videoId] = now;
                    }
                }
                else
                {
                    _visibleSince.Remove(videoId);
                }

                var dwell = ratio > 0 && _visibleSince.TryGetValue(videoId, out var since)
                    ? TimeSpan.FromMilliseconds(now - since)
                    : TimeSpan.Zero;

                candidates.Add(new MediaPrefetchCandidate
                {
                    VideoId = videoId,
                    Title = title,
                    IsPgc = isPgc,
                    VisibleRatio = ratio,
                    IsHovered = interaction.IsHovered,
                    IsFocused = interaction.IsFocused,
                    Index = order++,
                    Dwell = dwell,
                    IsNextScreen = isNextScreen,
                });
            }

            // 已经不在可视树里的卡片：解除事件、清掉悬停状态.
            var stale = _interactions.Keys.Where(p => !seen.Contains(p)).ToList();
            foreach (var card in stale)
            {
                DetachInteraction(card);
                _interactions.Remove(card);
            }

            scheduler.UpdateCandidates(_prefetchOwnerKey, candidates);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "预取：视口哨兵计算候选失败");
        }
    }

    private static void CollectCards(DependencyObject root, List<MomentCardControl> cards)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is MomentCardControl card)
            {
                cards.Add(card);
                continue;
            }

            CollectCards(child, cards);
        }
    }

    private static bool TryGetMedia(MomentItemViewModel viewModel, out string videoId, out string title, out bool isPgc)
    {
        videoId = string.Empty;
        title = string.Empty;
        isPgc = false;
        if (viewModel.FindInnerContent<VideoInformation>() is { } video)
        {
            videoId = video.Identifier.Id ?? string.Empty;
            title = video.Identifier.Title ?? string.Empty;
            return !string.IsNullOrEmpty(videoId);
        }

        if (viewModel.FindInnerContent<EpisodeInformation>() is { } episode)
        {
            videoId = episode.Identifier.Id ?? string.Empty;
            title = episode.Identifier.Title ?? string.Empty;
            isPgc = true;
            return !string.IsNullOrEmpty(videoId) && videoId != "0";
        }

        return false;
    }

    private static Rect? GetBoundsInHost(ScrollView host, FrameworkElement card)
    {
        try
        {
            var transform = card.TransformToVisual(host);
            return transform.TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double GetVisibleRatio(Rect? bounds, double viewportWidth, double viewportHeight)
    {
        if (bounds is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            return 0;
        }

        var left = Math.Max(rect.Left, 0);
        var right = Math.Min(rect.Right, viewportWidth);
        var top = Math.Max(rect.Top, 0);
        var bottom = Math.Min(rect.Bottom, viewportHeight);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        var visible = (right - left) * (bottom - top);
        var total = rect.Width * rect.Height;
        return total <= 0 ? 0 : Math.Clamp(visible / total, 0, 1);
    }

    private CardInteraction EnsureInteraction(MomentCardControl card)
    {
        if (_interactions.TryGetValue(card, out var existing))
        {
            return existing;
        }

        var interaction = new CardInteraction();
        card.PointerEntered += OnCardPointerEntered;
        card.PointerExited += OnCardPointerExited;
        card.GotFocus += OnCardGotFocus;
        card.LostFocus += OnCardLostFocus;
        _interactions[card] = interaction;
        return interaction;
    }

    private void DetachInteraction(MomentCardControl card)
    {
        card.PointerEntered -= OnCardPointerEntered;
        card.PointerExited -= OnCardPointerExited;
        card.GotFocus -= OnCardGotFocus;
        card.LostFocus -= OnCardLostFocus;
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is MomentCardControl card && _interactions.TryGetValue(card, out var interaction))
        {
            interaction.IsHovered = true;
        }
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is MomentCardControl card && _interactions.TryGetValue(card, out var interaction))
        {
            interaction.IsHovered = false;
        }
    }

    private void OnCardGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is MomentCardControl card && _interactions.TryGetValue(card, out var interaction))
        {
            interaction.IsFocused = true;
            QueuePrefetchEvaluation();
        }
    }

    private void OnCardLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is MomentCardControl card && _interactions.TryGetValue(card, out var interaction))
        {
            interaction.IsFocused = false;
        }
    }

    /// <summary>
    /// 一张卡的交互状态（悬停 / 焦点）.
    /// </summary>
    private sealed class CardInteraction
    {
        public bool IsHovered { get; set; }

        public bool IsFocused { get; set; }
    }
}
