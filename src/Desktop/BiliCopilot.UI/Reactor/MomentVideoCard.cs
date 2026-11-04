// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls;
using BiliCopilot.UI.Controls.Components;
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
/// 用 Reactor 重写的动态视频卡片.
/// 版式由 Reactor 排；头像按钮、封面、点赞/评论面板、更多按钮仍用现成的 XAML 控件.
/// </summary>
public sealed class MomentVideoCard : Component
{
    private readonly MomentItemViewModel _viewModel;
    private readonly bool _preserveCoverAspectRatio;

    /// <summary>
    /// Initializes a new instance of the <see cref="MomentVideoCard"/> class.
    /// </summary>
    public MomentVideoCard(MomentItemViewModel viewModel, bool preserveCoverAspectRatio)
    {
        _viewModel = viewModel;
        _preserveCoverAspectRatio = preserveCoverAspectRatio;
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = _viewModel;

        var userButton = new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Command = vm.ShowUserSpaceCommand,
                    Content = new BasicCoverImage
                    {
                        Source = vm.Avatar,
                        Width = 36,
                        Height = 36,
                        CornerRadius = new CornerRadius(18),
                        DecodeWidth = 100,
                    },
                };
                button.Style = Application.Current.Resources.TryGetValue("AvatarButtonStyle", out var style) ? style as Style : null;
                return button;
            },
            _ => { })
            .Size(36, 36)
            .VAlign(VerticalAlignment.Center);

        Element header = Grid(
            new[] { GridSize.Auto, GridSize.Star(), GridSize.Auto },
            new[] { GridSize.Auto },
            userButton.Grid(row: 0, column: 0),
            VStack(
                2,
                TextBlock(vm.Author ?? string.Empty).MaxLines(1),
                TextBlock(vm.Tip ?? string.Empty)
                    .FontSize(12)
                    .Foreground(Brush("TextFillColorTertiaryBrush"))
                    .MaxLines(1))
                .Grid(row: 0, column: 1)
                .Margin(12, 0, 0, 0)
                .VAlign(VerticalAlignment.Center),
            BuildDescriptionButton(vm).Grid(row: 0, column: 2));

        Element cover = new XamlHostElement(
            () =>
            {
                var panel = new VideoCoverPanel { PreserveAspectRatio = _preserveCoverAspectRatio };
                panel.Children.Add(new BasicCoverImage
                {
                    Source = vm.VideoCover,
                    DecodeWidth = 600,
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                });
                panel.Children.Add(CreateDurationBadge(vm));
                return panel;
            },
            _ => { })
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);

        Element badge = vm.IsOpenedBadgeVisible
            ? Border(
                TextBlock("最近打开")
                    .FontSize(11)
                    .Foreground(Brush("TextOnAccentFillColorPrimaryBrush")))
                .Background(Brush("AccentFillColorDefaultBrush"))
                .CornerRadius(4)
                .Padding(7, 3, 7, 3)
                .Margin(8)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Top)
            : TextBlock(string.Empty);

        var coverRow = Grid(
            new[] { GridSize.Star() },
            new[] { GridSize.Star() },
            cover,
            badge);

        Element footer = Grid(
            new[] { GridSize.Star(), GridSize.Auto },
            new[] { GridSize.Auto },
            new XamlHostElement(
                () => new MomentItemOperationPanel { ViewModel = vm },
                _ => { })
                .Grid(row: 0, column: 0),
            new XamlHostElement(() => CreateOpenButton(vm), _ => { })
                .Grid(row: 0, column: 1));

        return Grid(
            new[] { GridSize.Star() },
            new[] { GridSize.Auto, _preserveCoverAspectRatio ? GridSize.Auto : GridSize.Star(), GridSize.Auto, GridSize.Auto },
            header.Margin(0, 0, 0, 12).Grid(row: 0),
            coverRow.Margin(0, 0, 0, 12).Grid(row: 1),
            TextBlock(vm.VideoTitle ?? string.Empty)
                .FontSize(13)
                .MaxLines(1)
                .HAlign(HorizontalAlignment.Left)
                .Margin(0, 0, 0, 12)
                .Grid(row: 2),
            footer.Grid(row: 3))
            .Padding(12);
    }

    private static Brush Brush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static UIElement CreateDurationBadge(MomentItemViewModel vm)
        => new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.6 },
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = vm.VideoDuration ?? string.Empty,
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };

    private static Element BuildDescriptionButton(MomentItemViewModel vm)
        => new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new FluentIcons.WinUI.SymbolIcon { Symbol = FluentIcons.Common.Symbol.TextDescription, FontSize = 12 },
                    Visibility = string.IsNullOrEmpty(vm.Description?.Text) ? Visibility.Collapsed : Visibility.Visible,
                };
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, "展开全文");
                var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
                flyout.Content = new EmoteTextBlock { Text = vm.Description, MaxWidth = 400, MaxLines = 99 };
                button.Flyout = flyout;
                return button;
            },
            _ => { });


    private static Button CreateOpenButton(MomentItemViewModel vm)
    {
        var button = new Button
        {
            VerticalAlignment = VerticalAlignment.Center,
            Command = vm.OpenInBroswerCommand,
            Content = new FluentIcons.WinUI.SymbolIcon { Symbol = FluentIcons.Common.Symbol.Globe, FontSize = 14 },
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, "在浏览器打开");
        return button;
    }
}
