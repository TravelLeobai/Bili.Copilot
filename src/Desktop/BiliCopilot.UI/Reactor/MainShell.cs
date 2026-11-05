// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls;
using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
using Richasy.WinUIKernel.Share.ViewModels;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 使用 Microsoft.UI.Reactor 声明式外壳重写的主界面.
/// 侧栏宽度由自己控制（展开 240 / 折叠 52），不再依赖 NavigationView 的折叠状态，
/// 那条路在自定义标题栏下一直不稳.
/// </summary>
public sealed class MainShell : Component
{
    private const double ExpandedWidth = 240;

    private const double CollapsedWidth = 52;

    private const string FavoritesTag = "__favorites__";

    private const string ViewLaterTag = "__view_later__";

    private const string HistoryTag = "__history__";

    private readonly NavigationViewModel _viewModel;

    private readonly AccountViewModel _accountViewModel;

    private readonly Grid _contentHost;

    private readonly AppSearchBox _searchBox = new()
    {
        MinWidth = 0,
        MaxWidth = 420,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly HeaderAccountControl _accountControl = new();

    private readonly Button _backButton = CreateFlatIconButton("\uE72B", "返回");

    private readonly Button _compactSearchButton = CreateFlatIconButton("\uE721", "搜索");

    private readonly Dictionary<string, NavItemButton> _sideNavControls = new();

    private Action? _togglePaneAction;

    private Action? _openPaneAction;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainShell"/> class.
    /// </summary>
    public MainShell(
        NavigationViewModel viewModel,
        MainFrame mainFrame,
        OverlayFrame overlayFrame,
        AccountViewModel accountViewModel)
    {
        _viewModel = viewModel;
        _accountViewModel = accountViewModel;
        _accountControl.AvatarSize = 20;
        _contentHost = new Grid();
        _contentHost.Children.Add(mainFrame);
        _contentHost.Children.Add(overlayFrame);
        _backButton.Click += (_, _) => _viewModel.Back();
        _compactSearchButton.Click += (_, _) => OpenPaneAndFocusSearch();
    }

    /// <summary>
    /// 展开侧栏并把焦点送进搜索框（等侧栏动画走完再聚焦）.
    /// </summary>
    private void OpenPaneAndFocusSearch()
    {
        _openPaneAction?.Invoke();
        _ = Task.Run(async () =>
        {
            await Task.Delay(200).ConfigureAwait(false);
            _searchBox.DispatcherQueue?.TryEnqueue(() => _searchBox.Focus(FocusState.Programmatic));
        });
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var (isPaneOpen, setIsPaneOpen) = UseState(true);
        _togglePaneAction = () => setIsPaneOpen(!isPaneOpen);
        _openPaneAction = () => setIsPaneOpen(true);

        var viewModel = UseObservableTree(_viewModel);
        var account = UseObservableTree(_accountViewModel);
        _ = viewModel.NavItemsVersion;

        var sideWidth = isPaneOpen ? ExpandedWidth : CollapsedWidth;

        var menuItems = viewModel.MenuItems
            .Where(item => item.IsVisible)
            .Select(item => BuildSideNavItem(item, isPaneOpen))
            .ToArray();

        var footerItems = new List<Element>
        {
            BuildStaticNavItem("我的收藏", FluentIcons.Common.Symbol.StarLineHorizontal3, FavoritesTag, viewModel.IsFavoritesPage, isPaneOpen),
            BuildStaticNavItem("稍后再看", FluentIcons.Common.Symbol.CalendarClock, ViewLaterTag, viewModel.IsViewLaterPage, isPaneOpen),
            BuildStaticNavItem("历史记录", FluentIcons.Common.Symbol.History, HistoryTag, viewModel.IsHistoryPage, isPaneOpen),
        };

        footerItems.AddRange(viewModel.FooterItems
            .Where(item => item.IsVisible)
            .Select(item => BuildSideNavItem(item, isPaneOpen)));

        // 两个分支必须用不同 key：都是 XamlHostElement，不换 key 会被按类型复用，
        // 折叠后显示的仍是搜索框（只是被压窄），点下去弹的是搜索建议而不是展开侧栏.
        Element searchRow = isPaneOpen
            ? new XamlHostElement(() => _searchBox, _ => { })
                .HAlign(HorizontalAlignment.Stretch)
                .Margin(4, 8, 4, 8)
                .WithKey("sidebar-search-box")
            : new XamlHostElement(() => _compactSearchButton, _ => { })
                .Size(40, 40)
                .Margin(4, 8, 4, 8)
                .HAlign(HorizontalAlignment.Center)
                .WithKey("sidebar-search-icon");

        // 账号行：头像 40 + 间距 4，和导航项的文字起点（4 + 40 + 4 = 48）对齐.
        Element accountRow = HStack(
            4,
            new XamlHostElement(() => _accountControl, _ => { })
                .Size(40, 40)
                .VAlign(VerticalAlignment.Center),
            isPaneOpen
                ? TextBlock(account.MyProfile?.User?.Name ?? string.Empty)
                    .FontSize(14)
                    .MaxLines(1)
                    .VAlign(VerticalAlignment.Center)
                : (Element)TextBlock(string.Empty))
            .Padding(4, 0, 8, 0)
            .Height(44);

        Element accountButton = Button(accountRow, () => _accountControl.ShowAccountFlyout())
            .AutomationName(account.MyProfile?.User?.Name ?? "我的")
            .SubtleButton()
            .Padding(0)
            .Height(44)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch);

        // 侧栏三段式：搜索占顶、菜单居中滚动、收藏及以下整组贴底.
        Element sidebarBottom = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Auto],
            VStack(2, footerItems.ToArray()).Grid(row: 0),
            accountButton.Grid(row: 1))
            .VAlign(VerticalAlignment.Bottom);

        Element sidebar = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            searchRow.Grid(row: 0),
            VStack(2, menuItems).Grid(row: 1).VAlign(VerticalAlignment.Top),
            sidebarBottom.Grid(row: 2))
            .Width(sideWidth)
            .Padding(0, 0, 0, 8)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Stretch)
            .Background("Transparent");

        // 侧栏和内容之间一条竖线，参照 NavigationView 里 pane 与 content 的分界.
        Element sidebarDivider = Border(TextBlock(string.Empty))
            .Width(1)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Stretch)
            .Background("DividerStrokeColorDefaultBrush");

        return Grid(
            columns: [GridSize.Auto, GridSize.Px(1), GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            BuildTopBar(viewModel).Grid(row: 0, columnSpan: 3),
            sidebar.Grid(row: 1, column: 0),
            sidebarDivider.Grid(row: 1, column: 1),
            new XamlHostElement(() => _contentHost, _ => { })
                .Grid(row: 1, column: 2)
                .Padding(12, 0, 0, 0));
    }

    private Element BuildTopBar(NavigationViewModel viewModel)
        => HStack(
            4,
            viewModel.IsOverlayOpen
                ? new XamlHostElement(() => _backButton, _ => { })
                    .Size(40, 40)
                    .VAlign(VerticalAlignment.Center)
                : (Element)TextBlock(string.Empty),
            Button(
                TextBlock("\uE700").FontFamily("Segoe Fluent Icons").FontSize(14),
                () => _togglePaneAction?.Invoke())
                .Size(40, 40)
                .SubtleButton()
                .AutomationName("切换导航栏")
                .VAlign(VerticalAlignment.Center),
            TextBlock("哔哩助理")
                .SemiBold()
                .FontSize(14)
                .VAlign(VerticalAlignment.Center)
                .Margin(4, 0, 0, 0))
            // Padding 4 + 按钮一半 20 = 24，和侧栏导航图标的中心对齐.
            .Padding(4, 0, 0, 0)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center);

    /// <summary>
    /// 固定项的导航（收藏/稍后再看/历史记录），选中态由页面状态决定.
    /// </summary>
    private Element BuildStaticNavItem(
        string title,
        FluentIcons.Common.Symbol symbol,
        string tag,
        bool isSelected,
        bool isPaneOpen)
    {
        if (!_sideNavControls.TryGetValue(tag, out var control))
        {
            control = new NavItemButton();
            control.Root.Click += (_, _) => NavigateByTag(tag);
            _sideNavControls[tag] = control;
        }

        var navItem = control;
        return new XamlHostElement(
            () => navItem.Root,
            _ =>
            {
                navItem.Label = isPaneOpen ? title : string.Empty;
                navItem.Symbol = symbol;
                navItem.IsItemSelected = isSelected;
                navItem.Root.Visibility = Visibility.Visible;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(navItem.Root, title);
            })
            .WithKey("sidenav-" + tag);
    }

    private Element BuildSideNavItem(AppNavigationItemViewModel item, bool isPaneOpen)
    {
        if (!_sideNavControls.TryGetValue(item.PageKey, out var control))
        {
            control = new NavItemButton();
            control.Root.Click += (_, _) => NavigateByTag(item.PageKey);
            _sideNavControls[item.PageKey] = control;
        }

        var navItem = control;
        return new XamlHostElement(
            () => navItem.Root,
            _ =>
            {
                navItem.Label = isPaneOpen ? item.Title : string.Empty;
                navItem.Symbol = item.Symbol;
                navItem.IsItemSelected = item.IsSelected;
                navItem.Root.Visibility = item.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(navItem.Root, item.Title);
            })
            .WithKey("sidenav-" + item.PageKey);
    }

    private static Button CreateFlatIconButton(string glyph, string automationName)
    {
        var button = new Button
        {
            Width = 40,
            Height = 40,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, automationName);

        // 悬停反馈：之前按下去没反应时分不清是没点到还是逻辑没跑，这里给个明显的底色变化.
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.PointerEntered += (_, _) => button.Background = LookupBrush("SubtleFillColorSecondaryBrush") ?? transparent;
        button.PointerExited += (_, _) => button.Background = transparent;
        return button;
    }

    private static Brush? LookupBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : null;

    private void NavigateToSettings()
    {
        var item = _viewModel.FooterItems.FirstOrDefault(entry => entry.PageKey.Contains("SettingsPage"));
        item?.NavigateCommand.Execute(default);
    }

    private void NavigateByTag(string tag)
    {
        Serilog.Log.Information("导航项被点击：{Tag}", tag);

        switch (tag)
        {
            case FavoritesTag:
                _viewModel.NavigateToOver(typeof(FavoritesPage), default);
                return;
            case ViewLaterTag:
                _viewModel.NavigateToOver(typeof(ViewLaterPage), default);
                return;
            case HistoryTag:
                _viewModel.NavigateToOver(typeof(HistoryPage), default);
                return;
        }

        if (tag.Contains("settings", StringComparison.OrdinalIgnoreCase))
        {
            NavigateToSettings();
            return;
        }

        var item = _viewModel.MenuItems.Concat(_viewModel.FooterItems)
            .FirstOrDefault(entry => entry.PageKey == tag);
        item?.NavigateCommand.Execute(default);
    }
}
