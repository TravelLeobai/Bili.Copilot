// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 动态卡片瀑布流.
/// Reactor 负责列数与分布，每列用一个 XAML StackPanel 承载原有卡片，保持卡片布局不变.
/// </summary>
public sealed class MomentWaterfall : Component
{
    private const double TwoColumnWidth = 900d;
    private const double ThreeColumnWidth = 1250d;
    private const double LoadMoreThreshold = 600d;
    private const double CardGap = 12d;

    private readonly MomentUperSectionViewModel _viewModel;
    private ScrollView? _scrollView;

    /// <summary>
    /// 底部哨兵的「滚到底再补一批」触发器（瀑布流没有虚拟化，一次只建一屏的卡片）.
    /// </summary>
    private readonly MomentSentinelPager _pager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MomentWaterfall"/> class.
    /// </summary>
    public MomentWaterfall(MomentUperSectionViewModel viewModel)
    {
        _viewModel = viewModel;
        _pager = new MomentSentinelPager("综合", viewModel.LoadNextBatch);
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var viewModel = UseObservableTree(_viewModel);
        var (columnCount, setColumnCount) = UseState(1);

        var items = viewModel.Items;
        if (items.Count == 0)
        {
            return ScrollView(
                TextBlock(viewModel.IsLoading ? "正在加载…" : "暂无动态")
                    .FontSize(13)
                    .Opacity(0.6)
                    .Margin(24, 32, 0, 0));
        }

        var buckets = new List<MomentItemViewModel>[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            buckets[i] = new List<MomentItemViewModel>();
        }

        for (var i = 0; i < items.Count; i++)
        {
            buckets[i % columnCount].Add(items[i]);
        }

        var columnElements = new List<Element>(columnCount);
        for (var i = 0; i < columnCount; i++)
        {
            var bucket = buckets[i];
            Element column = new XamlHostElement(
                () => new StackPanel(),
                element => SyncColumn((StackPanel)element, bucket))
                .WithKey("column-" + i)
                .Margin(0, 0, i < columnCount - 1 ? CardGap : 0, 0)
                .Grid(row: 0, column: i);
            columnElements.Add(column);
        }

        var columnsElement = Grid(
            columns: Enumerable.Repeat(GridSize.Star(), columnCount).ToArray(),
            rows: new[] { GridSize.Auto },
            columnElements.ToArray())
            .OnSizeChanged((_, e) =>
            {
                var count = GetColumnCount(e.NewSize.Width);
                if (count != columnCount)
                {
                    setColumnCount(count);
                }
            });

        var footer = viewModel.IsLoading
            ? (Element)TextBlock("正在加载…").FontSize(12).Opacity(0.6).Margin(0, 12, 0, 12)
            : TextBlock(string.Empty);

        // 底部哨兵：1px 透明元素，挂载时由触发器找到外层 ScrollView；
        // 之后只有「滚到底 + 又往下滚了一段」才补 12 张，不滚就不动.
        var sentinel = new XamlHostElement(
            () => new Border { Height = 1, Opacity = 0, IsHitTestVisible = false },
            element => _pager.Attach(element))
            .WithKey("moment-waterfall-sentinel")
            .OnUnmountAdd(_ => _pager.Detach());

        // 数据一变就先检查内容有没有撑满一屏，没撑满继续补（补到撑满就停）；
        // 用户正停在底部时顺手放一批出来，不用再滚一下才动.
        UseEffect(
            () =>
            {
                _pager.QueueRevealIfAtBottom();
                _pager.QueueFillCheck();
            },
            items.Count);

        return ScrollView(
            VStack(
                8,
                columnsElement,
                footer,
                sentinel)
            .Margin(CardGap, CardGap, CardGap, CardGap))
            .VerticalScrollMode(ScrollingScrollMode.Enabled)
            .OnMountAdd(element =>
            {
                if (element is ScrollView scroll)
                {
                    _scrollView = scroll;
                    scroll.ViewChanged += OnScrollViewChanged;
                }
            })
            .OnUnmountAdd(element =>
            {
                if (element is ScrollView scroll)
                {
                    scroll.ViewChanged -= OnScrollViewChanged;
                    if (ReferenceEquals(_scrollView, scroll))
                    {
                        _scrollView = null;
                    }
                }
            });
    }

    private static int GetColumnCount(double width)
    {
        if (width >= ThreeColumnWidth)
        {
            return 3;
        }

        return width >= TwoColumnWidth ? 2 : 1;
    }

    private void OnScrollViewChanged(ScrollView sender, object args)
    {
        if (sender.ScrollableHeight - sender.VerticalOffset < LoadMoreThreshold)
        {
            // 还有没铺出来的卡片时先别去拉新数据：列表只有一屏高，「到底了」的判定会一直成立，
            // 就会一页接一页把整条动态拉干净；等滚到底把这一批补完再去拿下一页.
            if (_viewModel.HasHiddenItems)
            {
                return;
            }

            _viewModel.LoadItemsCommand.Execute(default);
        }
    }

    private static void SyncColumn(StackPanel panel, IReadOnlyList<MomentItemViewModel> items)
    {
        // 回收多余卡片时先解绑：让卡片里的图片控件把旧请求取消、旧源清掉，
        // 否则这些卡片被复用给新数据时，会先闪一下上一个内容的图.
        while (panel.Children.Count > items.Count)
        {
            var last = panel.Children.Count - 1;
            if (panel.Children[last] is MomentCardControl stale)
            {
                stale.ViewModel = null;
            }

            panel.Children.RemoveAt(last);
        }

        for (var i = 0; i < items.Count; i++)
        {
            MomentCardControl card;
            if (i < panel.Children.Count && panel.Children[i] is MomentCardControl existing)
            {
                card = existing;
            }
            else
            {
                card = new MomentCardControl
                {
                    Margin = new Thickness(0, 6, 0, 6),
                    Style = ResolveCardStyle(),
                };

                if (i < panel.Children.Count)
                {
                    panel.Children[i] = card;
                }
                else
                {
                    panel.Children.Add(card);
                }
            }

            // 身份比对：索引相同不等于同一条数据（插入/重排后索引会整体错位）。
            // 换数据时先解绑再绑，避免旧图在新卡片上残留一瞬；内容没变就不动，
            // 免得每次重排都重新请求一遍图片.
            if (!ReferenceEquals(card.Tag, items[i]))
            {
                if (card.Tag is not null && !ReferenceEquals(card.ViewModel, items[i]))
                {
                    card.ViewModel = null;
                }

                card.Tag = items[i];
                card.ViewModel = items[i];
            }
        }
    }

    private static Style? ResolveCardStyle()
    {
        if (Application.Current.Resources.TryGetValue("FullMomentCardStyle", out var value)
            && value is Style style)
        {
            return style;
        }

        return null;
    }
}

