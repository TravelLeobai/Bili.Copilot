// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Specialized;
using System.ComponentModel;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.Extensions;
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
    private ScrollView? _interactionScrollView;

    // 粉丝团勋章用小号字 + Segoe UI Variable Small，和 B 站直播间观感一致.
    private const string MedalFontFamily = "Segoe UI Variable Small";
    private readonly PlayerViewModel _viewModel;
    private int _version;
    private SelectorBar? _tabBar;
    private bool _suppressTabChange;
    private TextBox? _liveInputBox;

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
                void OnCollection(object? sender, NotifyCollectionChangedEventArgs e)
                {
                    // 新弹幕进来后滚到底，老弹幕自然往上顶.
                    if (ReferenceEquals(sender, vm.LiveInteractions))
                    {
                        ScrollToLatest();
                    }

                    setVersion(++_version);
                }
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

        UseEffect(
            () =>
            {
                // 新弹幕进来后滚到底，老弹幕自然往上顶.
                ScrollToLatest();
                return () => { };
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
        // 头像地址可能没回来，也可能是 //i0.hdslb.com/... 这种非绝对地址，
        // 直接塞给 Reactor 的 Image 会抛「不是有效的绝对 URI」，统一走兜底.
        var avatar = AvatarOrFallback(vm.LiveUserAvatarUrl, vm.LiveUser?.Name, 40, 16);

        var nameBlock = VStack(
            2,
            TextBlock(vm.LiveUser?.Name ?? "未开播").FontSize(14).FontWeight(FontWeights.SemiBold),
            HStack(
                10,
                TextBlock($"在线 {vm.LiveOnlineText}").FontSize(11).Opacity(0.7),
                TextBlock($"粉丝 {vm.LiveFansText}").FontSize(11).Opacity(0.7),
                TextBlock($"已播 {vm.LiveUptimeText}").FontSize(11).Opacity(0.7)));

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

        // 头像占左列，关注按钮压在头像下面；画质线路和关注按钮同一行.
        return Grid(
            new[] { GridSize.Auto, GridSize.Star() },
            new[] { GridSize.Auto, GridSize.Auto },
            avatar.Grid(row: 0, column: 0).Margin(0, 0, 0, 6),
            nameBlock.Grid(row: 0, column: 1).Margin(10, 0, 0, 6),
            followHost.Grid(row: 1, column: 0).HAlign(HorizontalAlignment.Center),
            qualityHost.Grid(row: 1, column: 1).HAlign(HorizontalAlignment.Left).Margin(10, 0, 0, 0))
            .Padding(12, 10, 12, 8);
    }

    /// <summary>
    /// 互动 / 榜单 / 大航海，用 SelectorBar，和动态页顶栏的「视频 / 综合」同一种样式.
    /// </summary>
    private Element BuildTabs(PlayerViewModel vm)
    {
        var selectorHost = new XamlHostElement(
            () => CreateTabSelector(vm),
            _ => SyncTabSelection(vm))
            .VAlign(VerticalAlignment.Center);

        // 刷新按钮已经挪到面板顶栏（和关闭按钮同一行），这里只留选项卡.
        return Grid(
            new[] { GridSize.Star() },
            new[] { GridSize.Auto },
            selectorHost.Grid(row: 0, column: 0))
            .Padding(12, 0, 12, 8);
    }
    private SelectorBar CreateTabSelector(PlayerViewModel vm)
    {
        if (_tabBar is not null)
        {
            return _tabBar;
        }

        var bar = new SelectorBar { VerticalAlignment = VerticalAlignment.Center };
        foreach (var title in new[] { "互动", "榜单", "大航海" })
        {
            bar.Items.Add(new SelectorBarItem { Text = title });
        }

        bar.SelectionChanged += (sender, _) =>
        {
            if (_suppressTabChange || sender is not SelectorBar selector)
            {
                return;
            }

            var index = selector.Items.IndexOf(selector.SelectedItem);
            if (index >= 0)
            {
                vm.SwitchLiveTabCommand.Execute(index);
            }
        };

        _tabBar = bar;
        return bar;
    }

    private void SyncTabSelection(PlayerViewModel vm)
    {
        if (_tabBar is null || !_tabBar.Items.Any())
        {
            return;
        }

        var target = _tabBar.Items[Math.Clamp(vm.LivePanelTab, 0, _tabBar.Items.Count - 1)];
        if (ReferenceEquals(_tabBar.SelectedItem, target))
        {
            return;
        }

        _suppressTabChange = true;
        _tabBar.SelectedItem = target;
        _suppressTabChange = false;
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

        var list = ScrollView(VStack(0, [.. rows]).Padding(12, 0, 12, 8))
            .Padding(0)
            .VerticalScrollMode(ScrollingScrollMode.Enabled)
            .OnMountAdd(element =>
            {
                if (element is ScrollView scroll)
                {
                    _interactionScrollView = scroll;
                    ScrollToLatest();
                }
            })
            .OnUnmountAdd(element =>
            {
                if (ReferenceEquals(_interactionScrollView, element))
                {
                    _interactionScrollView = null;
                }
            });

        return list;
    }

    /// <summary>
    /// 把互动列表滚到最底部（最新一条）.
    /// </summary>
    private void ScrollToLatest()
    {
        var scroll = _interactionScrollView;
        if (scroll is null)
        {
            return;
        }

        _ = scroll.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => scroll.ScrollTo(0, scroll.ScrollableHeight));
    }

    private Element BuildFooter(PlayerViewModel vm)
    {
        // 底栏：[弹][礼][表情][输入 *][发送]，输入框吃掉中间所有宽度，整体撑满面板.
        return Grid(
            new[] { GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto },
            new[] { GridSize.Auto },
            BuildDanmakuToggle(vm).Grid(row: 0, column: 0).Margin(0, 0, 6, 0),
            BuildGiftButton(vm).Grid(row: 0, column: 1).Margin(0, 0, 6, 0),
            BuildEmoteButton(vm).Grid(row: 0, column: 2).Margin(0, 0, 6, 0),
            BuildInputBox(vm).Grid(row: 0, column: 3).Margin(0, 0, 6, 0),
            BuildSendButton(vm).Grid(row: 0, column: 4))
            .Padding(12, 8, 12, 12);
    }

    private static Element BuildDanmakuToggle(PlayerViewModel vm)
        => new XamlHostElement(
            () =>
            {
                var button = new ToggleButton
                {
                    Content = "弹",
                    Width = 32,
                    Height = 32,
                    Padding = new Thickness(0),
                    IsChecked = vm.IsLiveDanmakuVisible,
                };
                button.Click += (_, _) => vm.SwitchLiveDanmakuCommand.Execute(default);
                return button;
            },
            element => ((ToggleButton)element).IsChecked = vm.IsLiveDanmakuVisible)
            .WithKey("live-danmaku-toggle");

    private Element BuildGiftButton(PlayerViewModel vm)
        => new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Width = 32,
                    Height = 32,
                    Padding = new Thickness(0),
                    Content = new FluentIcons.WinUI.SymbolIcon
                    {
                        Symbol = FluentIcons.Common.Symbol.Gift,
                        FontSize = 14,
                    },
                };
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, "我的礼物");
                button.Click += (_, _) => ShowGiftFlyout(button);
                return button;
            },
            _ => { })
            .WithKey("live-gift");

    private Element BuildInputBox(PlayerViewModel vm)
        => new XamlHostElement(
            () =>
            {
                var box = new TextBox
                {
                    PlaceholderText = "发个弹幕…",
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

                // 输入过程中不往 ViewModel 回写：回写会触发重渲染，重建/重置输入框导致吞字.
                box.KeyDown += (s, args) =>
                {
                    if (args.Key == Windows.System.VirtualKey.Enter)
                    {
                        args.Handled = true;
                        SendLiveInput(vm);
                    }
                };

                _liveInputBox = box;
                return box;
            },
            element =>
            {
                var box = (TextBox)element;
                _liveInputBox = box;

                // 只在没聚焦时同步外部值（例如发送成功后清空）.
                if (box.FocusState == FocusState.Unfocused
                    && box.Text != (vm.LiveDanmakuInput ?? string.Empty))
                {
                    box.Text = vm.LiveDanmakuInput ?? string.Empty;
                }
            })
            .HAlign(HorizontalAlignment.Stretch)
            .WithKey("live-input");

    private void SendLiveInput(PlayerViewModel vm)
    {
        var text = _liveInputBox?.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        vm.LiveDanmakuInput = text;
        vm.SendLiveDanmakuCommand.Execute(default);

        // 发送后本地先清空，不等 ViewModel 回调（重渲染期间光标会乱）.
        if (_liveInputBox is not null)
        {
            _liveInputBox.Text = string.Empty;
        }
    }

    private Element BuildSendButton(PlayerViewModel vm)
        => new XamlHostElement(
            () =>
            {
                var button = new Button { Content = "发送", Padding = new Thickness(12, 4, 12, 4), Height = 32 };
                // 点按钮时输入框还没回写 ViewModel，这里直接从框里取文本，跟回车走同一条路.
                button.Click += (_, _) => SendLiveInput(vm);
                return button;
            },
            _ => { })
            .WithKey("live-send");

    /// <summary>
    /// 一条互动消息.
    /// 进场/系统提示压缩成一行小字，其余按评论卡片的样子排版：头像 + 名字/勋章 + 正文.
    /// </summary>
    internal static Element BuildInteractionRow(LiveInteractionViewModel item)
    {
        if (item.Kind is LiveInteractionKind.Enter or LiveInteractionKind.System)
        {
            var line = item.Kind == LiveInteractionKind.System
                ? item.Text
                : !string.IsNullOrWhiteSpace(item.Text) && item.Text.Contains("进入")
                    ? item.Text
                    : $"{item.UserName} 进入直播间";

            return TextBlock(line)
                .FontSize(11)
                .Opacity(0.5)
                .TextWrapping(TextWrapping.Wrap)
                .Margin(2, 1, 2, 3)
                .WithKey("live-line-" + item.GetHashCode());
        }

        var isGift = item.Kind == LiveInteractionKind.Gift;
        var body = isGift
            ? $"送出 {item.GiftName} ×{item.GiftCount}（{item.GiftPrice} 金瓜子）"
            : item.Text;
        var nameColor = isGift || item.IsSuperChat
            ? "SystemFillColorAttentionBrush"
            : "TextFillColorPrimaryBrush";

        Element badge = item.HasMedal
            ? BuildMedalBadge(item)
            : (Element)TextBlock(string.Empty);
        var header = HStack(
            0,
            TextBlock(item.UserName)
                .FontSize(12)
                .FontWeight(FontWeights.SemiBold)
                .Foreground(ResolveBrush(nameColor))
                .VAlign(VerticalAlignment.Center),
            badge);

        var avatar = AvatarOrFallback(item.Avatar, item.UserName, 24, 11);

        // 表情弹幕：正文位置直接画表情图，和文字一样靠左，别被拉伸居中.
        Element bodyElement = item.HasEmote && NormalizeEmoteUrl(item.EmoteUrl) is { } emoteUri
            ? new XamlHostElement(
                () => new Image
                {
                    Width = 64,
                    Height = 64,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(emoteUri)),
                },
                _ => { })
                .HAlign(HorizontalAlignment.Left)
                .Margin(8, 2, 0, 0)
                .Grid(row: 1, column: 1)
            : TextBlock(body)
                .FontSize(12)
                .TextWrapping(TextWrapping.Wrap)
                .Margin(8, 2, 0, 0)
                .Grid(row: 1, column: 1);

        var content = Grid(
            new[] { GridSize.Auto, GridSize.Star() },
            new[] { GridSize.Auto, GridSize.Auto },
            avatar
                .VAlign(VerticalAlignment.Top)
                .Grid(row: 0, rowSpan: 2, column: 0),
            header.Grid(row: 0, column: 1).Margin(8, 0, 0, 0),
            bodyElement);

        return Border(content)
            .Background(ResolveBrush("CardBackgroundFillColorDefaultBrush"))
            .CornerRadius(8)
            .Padding(8, 5, 8, 5)
            .Margin(0, 0, 0, 4)
            .WithKey("live-msg-" + item.GetHashCode());
    }

    /// <summary>
    /// 粉丝团勋章：名称用官方等级段配色，等级数字同色相但更亮、更淡.
    /// </summary>
    private static Element BuildMedalBadge(LiveInteractionViewModel item)
    {
        var (nameColor, levelColor) = ResolveMedalColors(item.MedalLevel);

        Element content = item.MedalLevel > 0
            ? HStack(
                3,
                TextBlock(item.MedalName)
                    .FontSize(9)
                    .Foreground(new SolidColorBrush(nameColor))
                    .FontFamily(MedalFontFamily),
                TextBlock(item.MedalLevel.ToString())
                    .FontSize(9)
                    .SemiBold()
                    .Foreground(new SolidColorBrush(levelColor))
                    .FontFamily(MedalFontFamily))
            : TextBlock(item.MedalName)
                .FontSize(9)
                .Foreground(new SolidColorBrush(nameColor))
                .FontFamily(MedalFontFamily);

        return Border(content)
            .Background(ResolveBrush("ControlFillColorSecondaryBrush"))
            .CornerRadius(3)
            .Padding(4, 0, 4, 0)
            .Margin(6, 0, 0, 0)
            .VAlign(VerticalAlignment.Center);
    }

    /// <summary>
    /// 官方粉丝团等级配色（名称色）与等级数字色（亮一档、饱和降一档）.
    /// </summary>
    private static (Windows.UI.Color Name, Windows.UI.Color Level) ResolveMedalColors(int level)
    {
        var nameColor = level switch
        {
            <= 0 => Windows.UI.Color.FromArgb(255, 0x6E, 0x6E, 0x6E),
            <= 4 => Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8A),
            <= 8 => Windows.UI.Color.FromArgb(255, 0x35, 0x8D, 0xD4),
            <= 12 => Windows.UI.Color.FromArgb(255, 0x8A, 0x5A, 0xC8),
            <= 16 => Windows.UI.Color.FromArgb(255, 0xD0, 0x7A, 0x2C),
            <= 20 => Windows.UI.Color.FromArgb(255, 0xCC, 0x4A, 0x46),
            <= 24 => Windows.UI.Color.FromArgb(255, 0xA0, 0x28, 0x24),
            _ => Windows.UI.Color.FromArgb(255, 0xB8, 0x91, 0x2A),
        };

        var levelColor = ColorToolkit.WithLightnessAndSaturation(nameColor, 0.20, 0.72);
        return (nameColor, levelColor);
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
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, GridUnitType.Star) });
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

    /// <summary>
    /// 把地址整理成可直接用的绝对地址；不可用的返回 null.
    /// </summary>
    /// <param name="url">原始地址.</param>
    /// <returns>绝对 http(s) 地址或 null.</returns>
    private static string? NormalizeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var value = url.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? value
            : null;
    }

    /// <summary>
    /// 头像：地址可用就画图，否则用首字占位.
    /// </summary>
    /// <param name="url">头像地址.</param>
    /// <param name="name">用户名.</param>
    /// <param name="size">边长.</param>
    /// <param name="fontSize">首字号.</param>
    /// <returns>元素.</returns>
    private static Element AvatarOrFallback(string? url, string? name, double size, double fontSize)
    {
        var normalized = NormalizeImageUrl(url);
        if (normalized is not null)
        {
            return Border(Image(normalized).Size(size, size).AccessibilityHidden())
                .Size(size, size)
                .CornerRadius(size / 2)
                .Background(ResolveBrush("ControlFillColorSecondaryBrush"));
        }

        var text = string.IsNullOrWhiteSpace(name) ? "?" : name[..1];
        return Border(
            TextBlock(text)
                .FontSize(fontSize)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center))
            .Size(size, size)
            .CornerRadius(size / 2)
            .Background(ResolveBrush("ControlFillColorSecondaryBrush"));
    }
    private static Brush ResolveBrush(string name)
    {
        if (Application.Current.Resources.TryGetValue(name, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
    private Element BuildEmoteButton(PlayerViewModel vm)
        => new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Width = 32,
                    Height = 32,
                    Padding = new Thickness(0),
                    Content = new FluentIcons.WinUI.SymbolIcon
                    {
                        Symbol = FluentIcons.Common.Symbol.Emoji,
                        FontSize = 14,
                    },
                };

                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, "表情");
                var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
                flyout.Opening += async (_, _) => await FillEmoteFlyoutAsync(vm, flyout);
                button.Flyout = flyout;
                return button;
            },
            _ => { })
            .WithKey("live-emote");

    private async Task FillEmoteFlyoutAsync(PlayerViewModel vm, Flyout flyout)
    {
        if (flyout.Content is Grid)
        {
            return;
        }

        var root = new global::Microsoft.UI.Xaml.Controls.Grid
        {
            MinWidth = 392,
            MaxWidth = 460,
            Height = 320,
            ColumnSpacing = 6,
        };

        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(52) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, GridUnitType.Star) });

        var categoryPanel = new StackPanel { Spacing = 4 };
        var categoryScroll = new ScrollViewer
        {
            Content = categoryPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.Children.Add(categoryScroll);

        var divider = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 1,
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = ResolveBrush("DividerStrokeColorDefaultBrush"),
        };
        global::Microsoft.UI.Xaml.Controls.Grid.SetColumn(divider, 1);
        root.Children.Add(divider);

        var emoteHost = new global::Microsoft.UI.Xaml.Controls.Grid();
        global::Microsoft.UI.Xaml.Controls.Grid.SetColumn(emoteHost, 2);
        root.Children.Add(emoteHost);

        flyout.Content = root;

        var loading = new TextBlock { Text = "加载表情中…", Opacity = 0.6, FontSize = 12, Margin = new Thickness(8, 8, 0, 0) };
        emoteHost.Children.Add(loading);

        List<EmoteCategory> categories;
        try
        {
            categories = await BuildEmoteCategoriesAsync(vm).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "加载直播表情面板失败");
            emoteHost.Children.Clear();
            emoteHost.Children.Add(new TextBlock { Text = "表情加载失败", Opacity = 0.6, FontSize = 12, Margin = new Thickness(8, 8, 0, 0) });
            return;
        }

        emoteHost.Children.Clear();

        if (categories.Count == 0)
        {
            emoteHost.Children.Add(new TextBlock { Text = "没有可用表情（可能需要登录）", Opacity = 0.6, FontSize = 12, Margin = new Thickness(8, 8, 0, 0) });
            return;
        }

        var pages = new List<FrameworkElement>();
        foreach (var category in categories)
        {
            var page = BuildEmoteGrid(category, vm, flyout);
            page.Visibility = Visibility.Collapsed;
            pages.Add(page);
            emoteHost.Children.Add(page);
        }

        void RenderEmotes(int index)
        {
            for (var i = 0; i < pages.Count; i++)
            {
                pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            }

            for (var i = 0; i < categoryPanel.Children.Count; i++)
            {
                if (categoryPanel.Children[i] is Button tab)
                {
                    tab.Background = i == index
                        ? ResolveBrush("AccentFillColorSecondaryBrush")
                        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
            }
        }

        for (var i = 0; i < categories.Count; i++)
        {
            var index = i;
            var tab = BuildEmoteCategoryTab(categories[index]);
            tab.Click += (_, _) => RenderEmotes(index);
            categoryPanel.Children.Add(tab);
        }

        RenderEmotes(0);
    }

    private async Task<List<EmoteCategory>> BuildEmoteCategoriesAsync(PlayerViewModel vm)
    {
        var categories = new List<EmoteCategory>();

        var mine = LiveEmoteStore.Mine.ToList();
        if (mine.Count > 0)
        {
            categories.Add(new EmoteCategory("我的表情", mine));
        }

        var recent = LiveEmoteStore.Recent.ToList();
        if (recent.Count > 0)
        {
            categories.Add(new EmoteCategory("最近使用", recent));
        }

        // 直播间自己带的表情（emoticon_unique），点了直接发出去.
        if (vm.LiveEmotes.Count > 0)
        {
            categories.Add(new EmoteCategory("直播间", vm.LiveEmotes.Select(StoredEmote.FromLive).ToList()));
        }

        var roomEmotes = await BiliCopilot.UI.Services.LiveEmoteService.GetRoomEmotesAsync(
            vm.LiveRoomIdText,
            vm.LiveCookieText).ConfigureAwait(true);
        foreach (var package in roomEmotes)
        {
            var items = package.Emotes
                .Select(p => StoredEmote.FromToken(p.Token, p.Url, p.Description))
                .ToList();
            if (items.Count > 0)
            {
                categories.Add(new EmoteCategory(package.Name, items));
            }
        }

        var commentEmotes = BiliCopilot.UI.Services.BiliEmoteService.GetAll();
        if (commentEmotes.Count > 0)
        {
            categories.Add(new EmoteCategory(
                "常用表情",
                commentEmotes.Select(p => StoredEmote.FromToken(p.Token, p.Url)).ToList()));
        }

        return categories;
    }

    private Button BuildEmoteCategoryTab(EmoteCategory category)
    {
        var first = category.Emotes.FirstOrDefault();
        var url = NormalizeEmoteUrl(first?.Url);

        FrameworkElement content;
        if (url is not null)
        {
            content = new Image
            {
                Width = 28,
                Height = 28,
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url)),
            };
        }
        else
        {
            content = new TextBlock
            {
                Text = category.Name.Length > 2 ? category.Name[..2] : category.Name,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        var tab = new Button
        {
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = content,
        };

        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tab, category.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab, category.Name);
        return tab;
    }

    private FrameworkElement BuildEmoteGrid(EmoteCategory category, PlayerViewModel vm, Flyout flyout)
    {
        // 每行固定 7 个，手动排：ItemsRepeater + UniformGridLayout 在 Flyout 里宽度约束不住，
        // 列宽会被压成不均匀的窄条，表情图变形.
        const int perRow = 7;

        var wrap = new StackPanel { Spacing = 2 };
        var items = category.Emotes;
        for (var i = 0; i < items.Count; i += perRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var emote in items.Skip(i).Take(perRow))
            {
                row.Children.Add(BuildEmoteTile(emote, vm, flyout));
            }

            wrap.Children.Add(row);
        }

        return new ScrollViewer
        {
            Content = wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }
    private Button BuildEmoteTile(StoredEmote emote, PlayerViewModel vm, Flyout flyout)
    {
        var url = NormalizeEmoteUrl(emote.Url);
        FrameworkElement content;
        if (url is not null)
        {
            content = new Image
            {
                Width = 28,
                Height = 28,
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url)),
            };
        }
        else
        {
            content = new TextBlock
            {
                Text = emote.Text,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        var tile = new Button
        {
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = content,
        };

        var tooltip = emote.IsLocked ? $"{emote.Text}（未解锁）" : emote.Text;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tile, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, emote.Text);

        // 右键收藏/取消收藏，做成自己的表情.
        var menu = new MenuFlyout();
        var mineItem = new MenuFlyoutItem
        {
            Text = LiveEmoteStore.IsMine(emote.Key) ? "从我的表情移除" : "加入我的表情",
        };
        mineItem.Click += (_, _) =>
        {
            if (LiveEmoteStore.IsMine(emote.Key))
            {
                LiveEmoteStore.RemoveMine(emote.Key);
            }
            else
            {
                LiveEmoteStore.AddMine(emote);
            }

            flyout.Hide();
        };
        menu.Items.Add(mineItem);
        tile.ContextFlyout = menu;

        tile.Click += (_, _) =>
        {
            LiveEmoteStore.AddRecent(emote);
            if (!string.IsNullOrEmpty(emote.Unique))
            {
                var live = vm.LiveEmotes.FirstOrDefault(p => string.Equals(p.Unique, emote.Unique, StringComparison.Ordinal))
                    ?? new LiveEmoteItem
                    {
                        Unique = emote.Unique,
                        Text = emote.Text,
                        Url = emote.Url,
                        IsLocked = emote.IsLocked,
                    };

                vm.SendLiveEmoteCommand.Execute(live);
            }
            else
            {
                InsertEmoteToken(emote.Text);
            }

            flyout.Hide();
        };

        return tile;
    }

    private sealed record EmoteCategory(string Name, List<StoredEmote> Emotes);

    private void InsertEmoteToken(string token)
    {
        var box = _liveInputBox;
        if (box is null)
        {
            return;
        }

        box.Text = string.IsNullOrEmpty(box.Text) ? token : box.Text + token;
        box.SelectionStart = box.Text.Length;
        _ = box.Focus(FocusState.Programmatic);
    }
    private static string? NormalizeEmoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var value = url.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? value
            : null;
    }
}
