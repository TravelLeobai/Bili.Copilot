// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Specialized;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.Extensions;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 播放器弹幕列表.
/// 卡片样式与评论卡片保持一致：头像 + 昵称 + 时间戳 + 内容 + 操作按钮.
/// </summary>
public sealed class DanmakuPanel : Component
{
    private readonly VideoPlayerDanmakuSectionDetailViewModel _viewModel;
    private int _version;

    /// <summary>
    /// Initializes a new instance of the <see cref="DanmakuPanel"/> class.
    /// </summary>
    public DanmakuPanel(VideoPlayerDanmakuSectionDetailViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var viewModel = UseObservableTree(_viewModel);
        var (version, setVersion) = UseState(0);
        _ = version;

        UseEffect(
            () =>
            {
                void OnChanged(object? sender, EventArgs e) => setVersion(++_version);
                void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => setVersion(++_version);
                viewModel.Items.CollectionChanged += OnCollectionChanged;
                viewModel.PropertyChanged += OnChanged;
                return () =>
                {
                    viewModel.Items.CollectionChanged -= OnCollectionChanged;
                    viewModel.PropertyChanged -= OnChanged;
                };
            },
            Array.Empty<object>());

        void Refresh() => setVersion(++_version);

        var cards = new List<Element>();
        foreach (var item in viewModel.Items)
        {
            cards.Add(CreateCard(viewModel, item, Refresh));
        }

        Element status = viewModel.IsLoading
            ? TextBlock("正在加载弹幕…").FontSize(12).Opacity(0.6).Margin(0, 8, 0, 0).HAlign(HorizontalAlignment.Center)
            : viewModel.IsEmpty
                ? TextBlock("这个视频还没有弹幕").FontSize(13).Opacity(0.6).Margin(0, 24, 0, 0).HAlign(HorizontalAlignment.Center)
                : TextBlock(string.Empty);

        return Grid(
            columns: new[] { GridSize.Star() },
            rows: new[] { GridSize.Auto, GridSize.Star() },
            status.Grid(row: 0, column: 0),
            ScrollView(VStack(0, cards.ToArray()).Padding(12, 4, 12, 12))
                .Grid(row: 1, column: 0)
                .Padding(0)
                .VerticalScrollMode(ScrollingScrollMode.Enabled));
    }

    private static Element CreateCard(
        VideoPlayerDanmakuSectionDetailViewModel viewModel,
        DanmakuListItemViewModel item,
        Action refresh)
    {
        Element avatar = string.IsNullOrEmpty(item.Avatar?.ToString())
            ? Border(
                TextBlock("弹")
                    .FontSize(12)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center))
                .Size(36, 36)
                .CornerRadius(18)
                .Background(ResolveBrush("CardBackgroundFillColorSecondaryBrush"))
                .AccessibilityHidden()
            : Border(
                Image(item.Avatar.ToString())
                    .Set(image => image.Source = SmallImageCache.Get(item.Avatar.ToString(), 36) ?? image.Source)
                    .Size(36, 36)
                    .AccessibilityHidden())
                .CornerRadius(18);

        var nameBlock = TextBlock(item.UserName)
            .FontSize(13)
            .SemiBold()
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .MaxWidth(150)
            .VAlign(VerticalAlignment.Center);

        var levelBadge = item.Level > 0
            ? Border(
                TextBlock(item.LevelText)
                    .FontSize(9)
                    .Foreground(ResolveBrush("TextFillColorSecondaryBrush")))
                .CornerRadius(3)
                .Padding(4, 0, 4, 0)
                .Margin(6, 0, 0, 0)
                .WithBorder(ResolveBrush("CardStrokeColorDefaultBrush"), 1)
                .VAlign(VerticalAlignment.Center)
            : (Element)TextBlock(string.Empty);

        var timeButton = Button(
            TextBlock(item.TimeText).FontSize(11),
            () => viewModel.SeekCommand.Execute(item))
            .AutomationName($"跳转到 {item.TimeText}")
            .SubtleButton()
            .Padding(6, 2, 6, 2);

        var nameRow = Grid(
            columns: new[] { GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto },
            rows: new[] { GridSize.Auto },
            nameBlock.Grid(row: 0, column: 0),
            levelBadge.Grid(row: 0, column: 1),
            TextBlock(string.Empty).Grid(row: 0, column: 2),
            timeButton.Grid(row: 0, column: 3));

        var content = TextBlock(item.Content)
            .FontSize(13)
            .TextWrapping(TextWrapping.Wrap)
            .Margin(0, 4, 0, 0);

        var blockButton = Button(
            TextBlock("屏蔽").FontSize(11),
            () =>
            {
                viewModel.BlockCommand.Execute(item);
                refresh();
            })
            .AutomationName("屏蔽")
            .SubtleButton()
            .Padding(8, 4, 8, 4);

        var actionRow = Grid(
            columns: new[] { GridSize.Star(), GridSize.Auto },
            rows: new[] { GridSize.Auto },
            TextBlock(string.Empty).Grid(row: 0, column: 0),
            blockButton.Grid(row: 0, column: 1))
            .Margin(0, 6, 0, 0);
        var body = Grid(
            columns: new[] { GridSize.Star() },
            rows: new[] { GridSize.Auto, GridSize.Auto, GridSize.Auto },
            nameRow.Grid(row: 0, column: 0),
            content.Grid(row: 1, column: 0),
            actionRow.Grid(row: 2, column: 0));

        return Border(
            Grid(
                columns: new[] { GridSize.Auto, GridSize.Star() },
                rows: new[] { GridSize.Auto },
                avatar.Grid(row: 0, column: 0).VAlign(VerticalAlignment.Top).Margin(0, 0, 10, 0),
                body.Grid(row: 0, column: 1)))
            .Background(ResolveBrush("CardBackgroundFillColorDefaultBrush"))
            .CornerRadius(4)
            .Padding(12)
            .Margin(0, 0, 0, 8)
            .WithKey($"danmaku-{item.Uid}-{item.PositionSeconds:0.###}");
    }

    private static Brush ResolveBrush(string name)
    {
        if (Application.Current.Resources.TryGetValue(name, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
