// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 视频卡片版式.
/// </summary>
public enum VideoCardLayout
{
    /// <summary>推荐流大卡片：封面在上，信息在下的 116 高面板.</summary>
    Recommend,

    /// <summary>横向卡片：左封面右信息，用于动态等窄列表.</summary>
    Moment,
}

/// <summary>
/// 用 Reactor 重写的视频卡片内容.
/// 封面沿用 XAML 的 BasicCoverImage（它负责解码宽度和占位），版式由 Reactor 排.
/// </summary>
public sealed class VideoCardBody : Component
{
    private readonly VideoItemViewModel _viewModel;
    private readonly VideoCardLayout _layout;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoCardBody"/> class.
    /// </summary>
    public VideoCardBody(VideoItemViewModel viewModel, VideoCardLayout layout)
    {
        _viewModel = viewModel;
        _layout = layout;
    }

    /// <inheritdoc/>
    public override Element Render()
        => _layout == VideoCardLayout.Moment ? RenderMoment() : RenderRecommend();

    private static Brush Brush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static string FormatCount(double? value)
    {
        var count = value ?? 0;
        return count >= 10000 ? $"{count / 10000:F1}万" : count.ToString("F0");
    }

    private Element Cover(double radiusTopLeft, double radiusBottomRight = 0)
    {
        var radius = radiusBottomRight > 0
            ? new CornerRadius(radiusTopLeft, radiusTopLeft, radiusBottomRight, radiusBottomRight)
            : new CornerRadius(radiusTopLeft);

        return new XamlHostElement(
            () => new BasicCoverImage
            {
                Source = _viewModel.Cover,
                DecodeWidth = 600,
                CornerRadius = radius,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            },
            _ => { })
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);
    }

    private Element DurationBadge(VerticalAlignment vertical, HorizontalAlignment horizontal)
        => string.IsNullOrWhiteSpace(_viewModel.Duration)
            ? TextBlock(string.Empty)
            : Border(TextBlock(_viewModel.Duration).FontSize(11).Foreground(Brush("TextOnAccentFillColorPrimaryBrush")))
                .Background(new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.6 })
                .CornerRadius(4)
                .Padding(4, 2, 4, 2)
                .Margin(8)
                .HAlign(horizontal)
                .VAlign(vertical);

    private Element RenderRecommend()
    {
        var info = VStack(
            8,
            TextBlock(_viewModel.Author ?? string.Empty)
                .FontSize(12)
                .Foreground(Brush("TextFillColorTertiaryBrush"))
                .MaxLines(1),
            TextBlock(_viewModel.Title ?? string.Empty)
                .FontSize(14)
                .MaxLines(2),
            HStack(
                8,
                TextBlock(string.IsNullOrWhiteSpace(_viewModel.TagName) ? string.Empty : $"#{_viewModel.TagName}")
                    .FontSize(12)
                    .Foreground(Brush("TextFillColorTertiaryBrush"))
                    .MaxLines(1),
                TextBlock($"▶ {FormatCount(_viewModel.PlayCount)}")
                    .FontSize(12)
                    .Foreground(Brush("TextFillColorTertiaryBrush"))))
            .Padding(16, 12, 16, 12)
            .HAlign(HorizontalAlignment.Stretch);

        var reason = string.IsNullOrWhiteSpace(_viewModel.RecommendReason)
            ? (Element)TextBlock(string.Empty)
            : Border(TextBlock(_viewModel.RecommendReason).FontSize(11).Foreground(Brush("TextOnAccentFillColorPrimaryBrush")))
                .Background(Brush("AccentFillColorTertiaryBrush"))
                .CornerRadius(4)
                .Padding(4, 2, 4, 2)
                .Margin(8)
                .HAlign(HorizontalAlignment.Left)
                .VAlign(VerticalAlignment.Bottom);

        return Grid(
            new[] { GridSize.Star() },
            new[] { GridSize.Star(), GridSize.Auto },
            Grid(
                new[] { GridSize.Star() },
                new[] { GridSize.Star() },
                Cover(4, 0),
                reason,
                DurationBadge(VerticalAlignment.Bottom, HorizontalAlignment.Right))
                .Grid(row: 0),
            info.Grid(row: 1));
    }

    private Element RenderMoment()
    {
        var info = VStack(
            6,
            TextBlock(_viewModel.Title ?? string.Empty)
                .FontSize(14)
                .SemiBold()
                .MaxLines(2),
            HStack(
                12,
                TextBlock($"▶ {FormatCount(_viewModel.PlayCount)}")
                    .FontSize(11)
                    .Foreground(Brush("TextFillColorTertiaryBrush")),
                TextBlock(_viewModel.Author ?? string.Empty)
                    .FontSize(11)
                    .Foreground(Brush("TextFillColorTertiaryBrush"))
                    .MaxLines(1)))
            .Grid(column: 1)
            .Padding(0, 8, 12, 8)
            .VAlign(VerticalAlignment.Top);

        return Grid(
            new[] { GridSize.Px(180), GridSize.Star() },
            new[] { GridSize.Star() },
            Grid(
                new[] { GridSize.Star() },
                new[] { GridSize.Star() },
                Cover(4),
                DurationBadge(VerticalAlignment.Bottom, HorizontalAlignment.Right))
                .Grid(row: 0, column: 0),
            info.Grid(row: 0, column: 1))
            .Height(120)
            .Padding(4)
            .Background(Brush("CardBackgroundFillColorDefaultBrush"))
            .CornerRadius(4);
    }
}
