// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Components;
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
    /// Initializes a new instance of the <see cref="MomentWaterfall"/> class.
    /// </summary>
    public MomentWaterfall(MomentUperSectionViewModel viewModel) => _viewModel = viewModel;

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

        return ScrollView(
            VStack(
                8,
                columnsElement,
                footer)
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
            _viewModel.LoadItemsCommand.Execute(default);
        }
    }

    private static void SyncColumn(StackPanel panel, IReadOnlyList<MomentItemViewModel> items)
    {
        while (panel.Children.Count > items.Count)
        {
            panel.Children.RemoveAt(panel.Children.Count - 1);
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

            card.ViewModel = items[i];
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
