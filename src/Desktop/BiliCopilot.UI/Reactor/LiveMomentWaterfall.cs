// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Moment;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 直播分区的卡片瀑布流.
/// 列数按控件实际宽度算（每列等分星宽），每列一个 XAML StackPanel 承载卡片；
/// 卡片高度跟内容走，标题两行也不会被裁。
/// </summary>
public sealed class LiveMomentWaterfall : Component
{
    private const double MinCardWidth = 240d;
    private const double CardGap = 16d;

    private readonly LiveMomentSectionDetailViewModel _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentWaterfall"/> class.
    /// </summary>
    /// <param name="viewModel">直播分区视图模型.</param>
    public LiveMomentWaterfall(LiveMomentSectionDetailViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_viewModel);
        var (columnCount, setColumnCount) = UseState(1);
        var items = vm.Items;

        if (items.Count == 0)
        {
            return ScrollView(
                TextBlock(vm.IsLoading ? "正在加载…" : "关注的主播现在都没在播，也没找到回放")
                    .FontSize(13)
                    .Opacity(0.6)
                    .Margin(24, 32, 0, 0))
                .VerticalScrollMode(ScrollingScrollMode.Enabled);
        }

        var buckets = new List<LiveSectionCardViewModel>[Math.Max(1, columnCount)];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = [];
        }

        for (var i = 0; i < items.Count; i++)
        {
            buckets[i % buckets.Length].Add(items[i]);
        }

        var columnElements = new List<Element>(buckets.Length);
        for (var i = 0; i < buckets.Length; i++)
        {
            var bucket = buckets[i];
            columnElements.Add(new XamlHostElement(
                () => new StackPanel { Spacing = CardGap },
                element => SyncColumn((StackPanel)element, bucket))
                .WithKey("live-column-" + i)
                .Grid(row: 0, column: i));
        }

        var columnsElement = Grid(
            columns: Enumerable.Repeat(GridSize.Star(), buckets.Length).ToArray(),
            rows: [GridSize.Auto],
            [.. columnElements])
            .OnSizeChanged((_, e) =>
            {
                var count = GetColumnCount(e.NewSize.Width);
                if (count != columnCount)
                {
                    setColumnCount(count);
                }
            });

        return ScrollView(
            columnsElement.Margin(CardGap, 4, CardGap, CardGap))
            .VerticalScrollMode(ScrollingScrollMode.Enabled);
    }

    private static int GetColumnCount(double width)
    {
        // 宽度里已经含左右 Margin，先扣掉.
        var available = Math.Max(MinCardWidth, width - (CardGap * 2));
        var count = (int)Math.Floor((available + CardGap) / (MinCardWidth + CardGap));
        return Math.Clamp(count, 1, 8);
    }

    private static void SyncColumn(StackPanel panel, IReadOnlyList<LiveSectionCardViewModel> items)
    {
        while (panel.Children.Count > items.Count)
        {
            panel.Children.RemoveAt(panel.Children.Count - 1);
        }

        for (var i = 0; i < items.Count; i++)
        {
            LiveCardControl card;
            if (i < panel.Children.Count && panel.Children[i] is LiveCardControl existing)
            {
                card = existing;
            }
            else
            {
                card = new LiveCardControl();
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
}

