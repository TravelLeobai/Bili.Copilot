// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Specialized;
using System.ComponentModel;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 直播侧栏面板.
/// 骨架与列表用 Reactor 重画，按钮、开关、输入框这些交互控件仍用 XAML 控件承载，
/// 保证手感和图标和外层一致.
/// </summary>
public sealed class LiveSidePanel : Microsoft.UI.Reactor.Core.Component
{
    private readonly PlayerViewModel _viewModel;
    private int _version;


    /// <summary>
    /// Initializes a new instance of the <see cref="LiveSidePanel"/> class.
    /// </summary>
    public LiveSidePanel(PlayerViewModel viewModel)
    {
        _viewModel = viewModel;


    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_viewModel);
        var (version, setVersion) = UseState(0);
        _ = version;

        UseEffect(
            () =>
            {
                void OnProperty(object? sender, PropertyChangedEventArgs e) => setVersion(++_version);
                void OnCollection(object? sender, NotifyCollectionChangedEventArgs e) => setVersion(++_version);
                vm.PropertyChanged += OnProperty;
                vm.LiveInteractions.CollectionChanged += OnCollection;
                vm.LiveRanks.CollectionChanged += OnCollection;
                vm.LiveGuards.CollectionChanged += OnCollection;
                return () =>
                {
                    vm.PropertyChanged -= OnProperty;
                    vm.LiveInteractions.CollectionChanged -= OnCollection;
                    vm.LiveRanks.CollectionChanged -= OnCollection;
                    vm.LiveGuards.CollectionChanged -= OnCollection;
                };
            },
            Array.Empty<object>());

        var header = BuildHeader(vm);
        var tabs = BuildTabs(vm);
        var list = BuildContent(vm);
        var footer = BuildFooter(vm);

        return Grid(
            new[] { GridSize.Star() },
            new[] { GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto },
            header.Grid(row: 0),
            tabs.Grid(row: 1),
            list.Grid(row: 2),
            footer.Grid(row: 3));
    }

    private Element BuildHeader(PlayerViewModel vm)
    {
        var avatar = Image(vm.LiveUserAvatar?.ToString() ?? string.Empty).Size(40, 40).AccessibilityHidden();

        var nameBlock = VStack(
            2,
            TextBlock(vm.LiveUser?.Name ?? "未开播").FontSize(14).FontWeight(FontWeights.SemiBold),
            HStack(
                8,
                TextBlock(vm.LiveOnlineText).FontSize(11).Opacity(0.7),
                TextBlock(vm.LiveFansText).FontSize(11).Opacity(0.7),
                TextBlock(vm.LiveUptimeText).FontSize(11).Opacity(0.7)));

        var followHost = new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Content = vm.FollowButtonText,
                    Padding = new Thickness(10, 4, 10, 4),
                    IsEnabled = vm.LiveUser is not null,
                };
                button.Click += (_, _) => vm.ToggleLiveFollowCommand.Execute(default);
                return button;
            },
            element =>
            {
                var button = (Button)element;
                button.Content = vm.FollowButtonText;
                button.IsEnabled = vm.LiveUser is not null;
            })
            .WithKey("live-follow");

        var qualityHost = new XamlHostElement(
            () =>
            {
                var button = new DropDownButton
                {
                    Content = vm.LiveQualityAndLineText,
                    Padding = new Thickness(10, 4, 10, 4),
                };
                button.Flyout = BuildQualityFlyout(vm);
                return button;
            },
            element =>
            {
                var button = (DropDownButton)element;
                button.Content = vm.LiveQualityAndLineText;
                button.Flyout = BuildQualityFlyout(vm);
            })
            .WithKey("live-quality");

        return VStack(
            8,
            HStack(10, avatar, nameBlock, followHost),
            HStack(8, qualityHost)).Padding(12, 10, 12, 8);
    }

    private Element BuildTabs(PlayerViewModel vm)
    {
        var titles = new[] { "互动", "榜单", "大航海" };
        var children = new List<Element>();
        for (var i = 0; i < titles.Length; i++)
        {
            var index = i;
            children.Add(new XamlHostElement(
                () =>
                {
                    var button = new ToggleButton
                    {
                        Content = titles[index],
                        Padding = new Thickness(10, 4, 10, 4),
                        IsChecked = vm.LivePanelTab == index,
                    };
                    button.Click += (_, _) => vm.SwitchLiveTabCommand.Execute(index);
                    return button;
                },
                element => ((ToggleButton)element).IsChecked = vm.LivePanelTab == index)
                .WithKey("live-tab-" + index));
        }

        children.Add(new XamlHostElement(
            () =>
            {
                var button = new Button { Content = "刷新", Padding = new Thickness(8, 4, 8, 4) };
                button.Click += (_, _) => vm.RefreshLiveInfoCommand.Execute(default);
                return button;
            },
            _ => { }).WithKey("live-refresh"));

        return HStack(6, [.. children]).Padding(12, 0, 12, 8);
    }

    private Element BuildContent(PlayerViewModel vm)
    {
        var rows = new List<Element>();
        switch (vm.LivePanelTab)
        {
            case 1:
                foreach (var rank in vm.LiveRanks)
                {
                    rows.Add(BuildRankRow(rank));
                }

                break;
            case 2:
                foreach (var guard in vm.LiveGuards)
                {
                    rows.Add(BuildGuardRow(guard));
                }

                break;
            default:
                foreach (var item in vm.LiveInteractions)
                {
                    rows.Add(BuildInteractionRow(item));
                }

                break;
        }

        if (rows.Count == 0)
        {
            rows.Add(TextBlock(vm.LivePanelTab switch
            {
                1 => "还没有榜单数据",
                2 => "还没有大航海数据",
                _ => "等一条弹幕…",
            }).FontSize(12).Opacity(0.6).HAlign(HorizontalAlignment.Center).Margin(0, 24, 0, 0));
        }

        return ScrollView(VStack(0, [.. rows]).Padding(12, 0, 12, 12))
            .Padding(0)
            .VerticalScrollMode(ScrollingScrollMode.Enabled);
    }

    private Element BuildFooter(PlayerViewModel vm)
    {
        return HStack(
            8,
            new XamlHostElement(
                () =>
                {
                    var button = new ToggleButton
                    {
                        Content = "弹",
                        Padding = new Thickness(8, 4, 8, 4),
                        IsChecked = vm.IsLiveDanmakuVisible,
                    };
                    button.Click += (_, _) => vm.SwitchLiveDanmakuCommand.Execute(default);
                    return button;
                },
                element => ((ToggleButton)element).IsChecked = vm.IsLiveDanmakuVisible).WithKey("live-danmaku-toggle"),
            new XamlHostElement(
                () =>
                {
                    var button = new Button { Content = "礼物", Padding = new Thickness(8, 4, 8, 4) };
                    button.Click += (_, _) => ShowGiftFlyout(button);
                    return button;
                },
                _ => { }).WithKey("live-gift"),
            new XamlHostElement(
                () =>
                {
                    var box = new TextBox
                    {
                        PlaceholderText = "发个弹幕…",
                        Text = vm.LiveDanmakuInput ?? string.Empty,
                    };
                    box.TextChanged += (s, _) => vm.LiveDanmakuInput = ((TextBox)s).Text;
                    return box;
                },
                element =>
                {
                    var box = (TextBox)element;
                    if (box.Text != vm.LiveDanmakuInput)
                    {
                        box.Text = vm.LiveDanmakuInput ?? string.Empty;
                    }
                }).WithKey("live-input"),
            new XamlHostElement(
                () =>
                {
                    var button = new Button { Content = "发送", Padding = new Thickness(10, 4, 10, 4) };
                    button.Click += (_, _) => vm.SendLiveDanmakuCommand.Execute(default);
                    return button;
                },
                _ => { }).WithKey("live-send")).Padding(12, 8, 12, 12);
    }

    private static Element BuildInteractionRow(LiveInteractionViewModel item)
    {
        var isGift = item.Kind == LiveInteractionKind.Gift;
        var headline = isGift
            ? $"{item.UserName} 送出 {item.GiftName} ×{item.GiftCount}"
            : item.UserName;
        var body = isGift ? $"{item.GiftPrice} 金瓜子" : item.Text;
        var color = isGift ? "SystemFillColorAttentionBrush" : "TextFillColorPrimaryBrush";

        return Border(
            Grid(
                new[] { GridSize.Star(), GridSize.Auto },
                new[] { GridSize.Auto, GridSize.Auto },
                TextBlock(headline)
                    .FontSize(12)
                    .FontWeight(FontWeights.SemiBold)
                    .Foreground(ResolveBrush(color))
                    .Grid(row: 0, column: 0),
                TextBlock(item.MedalName)
                    .FontSize(10)
                    .Opacity(0.55)
                    .Grid(row: 0, column: 1),
                TextBlock(body)
                    .FontSize(12)
                    .TextWrapping(TextWrapping.Wrap)
                    .Margin(0, 2, 0, 0)
                    .Grid(row: 1, column: 0, columnSpan: 2)))
            .Background(ResolveBrush("CardBackgroundFillColorDefaultBrush"))
            .CornerRadius(6)
            .Padding(8, 6, 8, 6)
            .Margin(0, 0, 0, 6)
            .WithKey("live-msg-" + item.GetHashCode());
    }

    private static Element BuildRankRow(LiveRankItemViewModel rank)
        => Border(
            Grid(
                new[] { GridSize.Auto, GridSize.Star(), GridSize.Auto },
                new[] { GridSize.Auto },
                TextBlock(rank.RankText)
                    .FontSize(12)
                    .FontWeight(FontWeights.Bold)
                    .Width(24)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
                TextBlock(rank.UserName)
                    .FontSize(12)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 1),
                TextBlock(rank.ScoreText)
                    .FontSize(11)
                    .Opacity(0.6)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 2)))
            .Background(ResolveBrush("CardBackgroundFillColorDefaultBrush"))
            .CornerRadius(6)
            .Padding(8, 6, 8, 6)
            .Margin(0, 0, 0, 6)
            .WithKey("live-rank-" + rank.Rank);

    private static Element BuildGuardRow(LiveGuardItemViewModel guard)
        => Border(
            Grid(
                new[] { GridSize.Auto, GridSize.Star() },
                new[] { GridSize.Auto },
                TextBlock(guard.GuardText)
                    .FontSize(11)
                    .Opacity(0.7)
                    .Width(44)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
                TextBlock(guard.UserName)
                    .FontSize(12)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 1)))
            .Background(ResolveBrush("CardBackgroundFillColorDefaultBrush"))
            .CornerRadius(6)
            .Padding(8, 6, 8, 6)
            .Margin(0, 0, 0, 6)
            .WithKey("live-guard-" + guard.UserName);

    private MenuFlyout BuildQualityFlyout(PlayerViewModel vm)
    {
        var flyout = new MenuFlyout();
        foreach (var option in vm.LiveStreamOptions)
        {
            var item = new MenuFlyoutItem { Text = option.DisplayText };
            var captured = option;
            item.Click += (_, _) => vm.SwitchLiveStreamCommand.Execute(captured);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "还没有可用线路", IsEnabled = false });
        }

        return flyout;
    }

    private void ShowGiftFlyout(Button anchor)
    {
        var panel = new StackPanel { MinWidth = 220, Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "我的礼物", FontSize = 12, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = _viewModel.LiveWalletText, FontSize = 11, Opacity = 0.7 });

        foreach (var gift in _viewModel.LiveGiftBag)
        {
            var row = new Microsoft.UI.Xaml.Controls.Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = gift.GiftName, FontSize = 12 };
            var count = new TextBlock { Text = "×" + gift.Number, FontSize = 12, Opacity = 0.7 };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(count, 1);
            row.Children.Add(name);
            row.Children.Add(count);
            panel.Children.Add(row);
        }

        if (_viewModel.LiveGiftBag.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "礼物袋是空的", FontSize = 11, Opacity = 0.6 });
        }

        var flyout = new Flyout { Content = panel };
        flyout.ShowAt(anchor);
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