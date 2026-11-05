// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls;
using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
using Richasy.WinUIKernel.Share.Base;
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
/// 负责标题栏、搜索栏、顶部导航、侧边导航与内容宿主.
/// </summary>
public sealed class MainShell : Component
{
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
    private readonly Button _compactSearchButton = CreateFlatIconButton("\uE721", "搜索");

    private readonly Button _railSearchButton = CreateFlatIconButton("\uE721", "搜索");
    private readonly Button _backButton = CreateFlatIconButton("\uE72B", "返回");
    private readonly Button _favoriteButton = CreateFlatIconButton("\uE734", "我的收藏");
    private readonly Button _viewLaterButton = CreateFlatIconButton("\uE823", "稍后再看");
    private readonly Button _historyButton = CreateFlatIconButton("\uE81C", "历史记录");
    private const string SearchRailTag = "__rail_search__";

    private Action? _togglePaneAction;


    // 侧栏项缓存：Reactor 每次 Render 都拿同一个控件实例，避免重建把动画打断.
    private readonly Dictionary<string, AppNavigationViewItem> _sideNavControls = new();
    private Action? _openPaneAction;
    private ElementRef<FrameworkElement>? _titleBarRef;

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
        _compactSearchButton.Click += (_, _) => OpenPaneAndFocusSearch();
        _railSearchButton.Click += (_, _) => OpenPaneAndFocusSearch();
        _backButton.Click += (_, _) => _viewModel.Back();
        _favoriteButton.Click += (_, _) => _viewModel.NavigateToOver(typeof(FavoritesPage), default);
        _viewLaterButton.Click += (_, _) => _viewModel.NavigateToOver(typeof(ViewLaterPage), default);
        _historyButton.Click += (_, _) => _viewModel.NavigateToOver(typeof(HistoryPage), default);
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

    /// <summary>
    /// 标题栏元素，供窗口设置拖拽区域.
    /// </summary>
    public FrameworkElement? TitleBarElement => _titleBarRef?.Current;

    /// <inheritdoc/>
    public override Element Render()
    {
        // 汉堡按钮按你要求删掉了，侧栏就固定窄栏（84），不再有展开态.
        var (isPaneOpen, setIsPaneOpen) = UseState(false);
        // 自绘宽度：展开 240、窄栏 84.
        // 自绘宽度：展开 240、窄栏 84.
        _togglePaneAction = () => setIsPaneOpen(!isPaneOpen);
        _openPaneAction = () => setIsPaneOpen(true);
        var titleBarRef = this.UseElementRef<FrameworkElement>();
        _titleBarRef = titleBarRef;
        var viewModel = UseObservableTree(_viewModel);
        var account = UseObservableTree(_accountViewModel);
        _ = viewModel.NavItemsVersion;

        var sideMenuItems = viewModel.MenuItems
            .Where(item => item.IsVisible)
            .Select(item => BuildSideNavItem(item, isPaneOpen))
            .ToArray();

        // footer：收藏 / 稍后再看 / 历史记录 三项受「顶部导航栏可见性」开关控制，
        // 后面跟着消息、设置，最下面才是账号头像.
        var sideFooterItems = new List<Element>();
        if (viewModel.IsTopNavBarShown)
        {
            sideFooterItems.Add(BuildStaticNavItem("我的收藏", FluentIcons.Common.Symbol.StarLineHorizontal3, "__favorites__", viewModel.IsFavoritesPage, isPaneOpen));
            sideFooterItems.Add(BuildStaticNavItem("稍后再看", FluentIcons.Common.Symbol.CalendarClock, "__view_later__", viewModel.IsViewLaterPage, isPaneOpen));
            sideFooterItems.Add(BuildStaticNavItem("历史记录", FluentIcons.Common.Symbol.History, "__history__", viewModel.IsHistoryPage, isPaneOpen));
        }

        sideFooterItems.AddRange(viewModel.FooterItems
            .Where(item => item.IsVisible)
            .Select(item => BuildSideNavItem(item, isPaneOpen)));

        // 头像只留图：HeaderAccountControl 里的昵称在控件加载后才生成，
        // 所以等它 Loaded 再递归藏掉那些 TextBlock.
        Element accountRow = new XamlHostElement(() => _accountControl, control =>
            {
                control.MinWidth = 0;
                control.Width = 32;
                control.Height = 32;
                control.HorizontalAlignment = HorizontalAlignment.Center;
                control.VerticalAlignment = VerticalAlignment.Center;
                control.Loaded += (_, _) =>
                {
                    HideDescendantTexts(control);

                    // 光藏文字还不够，控件内层布局仍会把它算进宽度，再裁一刀保险.
                    control.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
                    {
                        Rect = new Windows.Foundation.Rect(0, 0, 32, 32),
                    };
                };
            })
            .Size(32, 32)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        Element accountPanel = Button(accountRow, () => _accountControl.ShowAccountFlyout())
            .AutomationName(account.MyProfile?.User?.Name ?? "我的")
            .SubtleButton()
            .Padding(0)
            .HorizontalContentAlignment(HorizontalAlignment.Center)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 8, 0, 0);

        // 侧栏顶部：展开时是完整搜索框，收起时只剩一个搜索图标.
        Element paneHeader = isPaneOpen
            ? new XamlHostElement(() => _searchBox, _ => { })
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Center)
                .Margin(12, 6, 8, 8)
            : (Element)TextBlock(string.Empty);

        // 侧栏整体三段：菜单占顶、中间留白、footer 组 + 头像贴底.
        // 不再用 NavigationView 的 PaneFooter —— 那玩意儿贴不贴底由模板说了算，
        // 自己排 Grid 才拿得住.
        // 菜单放 PaneCustomContent，footer 组 + 头像走 PaneFooter（NavigationView 保证贴底）.
        Element sideNav = VStack(2, sideMenuItems)
            .Margin(2, 8, 12, 0)
            .VAlign(VerticalAlignment.Top);
        Element sideFooterGroup = VStack(2, [.. sideFooterItems, accountPanel])
            .Margin(2, 0, 12, 8)
            .VAlign(VerticalAlignment.Bottom);

        // 侧栏不用 NavigationView 的 pane 了：整块自己排两列，
        // 中间一行吃 Star，footer 才会真正贴底（NavigationView 那条路试过，贴不住）.
        var titleContent = HStack(
            4,
            viewModel.IsOverlayOpen
                ? new XamlHostElement(() => _backButton, _ => { })
                    .Size(40, 40)
                    .HAlign(HorizontalAlignment.Left)
                    .VAlign(VerticalAlignment.Center)
                : (Element)TextBlock(string.Empty),
            (Element)TextBlock(string.Empty),
            TextBlock("哔哩助理").SemiBold().FontSize(14).VAlign(VerticalAlignment.Center))
            .VAlign(VerticalAlignment.Center)
            .HAlign(HorizontalAlignment.Left)
            .Grid(row: 0, column: 0);

        var titleBar = Grid(
            columns: [GridSize.Auto, GridSize.Star()],
            rows: [GridSize.Star()],
            HStack(4, titleContent)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),
            HStack(0, TextBlock(string.Empty))
                .Padding(0, 0, 12, 0)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 1))
            .Height(48)
            .Ref(titleBarRef);

var sideWidth = isPaneOpen ? 200 : 72;

        Element sideMenuColumn = VStack(2, sideMenuItems)
            .Grid(row: 0)
            .VAlign(VerticalAlignment.Top)
            .Margin(2, 8, 6, 0);

        Element sideFooterColumn = VStack(2, [.. sideFooterItems, accountPanel])
            .Grid(row: 2)
            .Margin(2, 0, 6, 8);

        Element sidebar = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            sideMenuColumn,
            sideFooterColumn)
            .Width(sideWidth)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Stretch);

        // 内容区一层亚克力：AcrylicBackgroundFillColorDefaultBrush 是 WinUI 的
        // 主题资源，系统亮色/暗色切换时它会自己换，不用我们判断.
        Element contentCard = Border(new XamlHostElement(() => _contentHost, _ => { }))
            .Background(LookupBrush("AcrylicBackgroundFillColorDefaultBrush"))
            .CornerRadius(12)
            .Margin(6, 0, 8, 8)
            .Grid(row: 1, column: 1);

        return Grid(
            columns: [GridSize.Px(sideWidth), GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            titleBar.Grid(row: 0, columnSpan: 2),
            sidebar.Grid(row: 1, column: 0),
            contentCard);
    }

    // 主题资源取刷子：AcrylicBackgroundFillColorDefaultBrush 会跟着亮/暗自动换.
    private static Brush? LookupBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : null;

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
        return button;
    }

    private static string? GetSymbolName(string pageKey) => pageKey switch
    {
        _ when pageKey.Contains("PopularPage") => "Home",
        _ when pageKey.Contains("MomentPage") => "Message",
        _ when pageKey.Contains("VideoPartitionPage") => "Video",
        _ when pageKey.Contains("LivePartitionPage") => "Camera",
        _ when pageKey.Contains("AnimePage") => "Play",
        _ when pageKey.Contains("CinemaPage") => "Pictures",
        _ when pageKey.Contains("ArticlePartitionPage") => "Page",
        _ when pageKey.Contains("MessagePage") => "Message",
        _ when pageKey.Contains("SettingsPage") => "Setting",
        _ when pageKey.Contains("DownloadPage") => "Download",
        _ when pageKey.Contains("LivePlayerPage") => "Camera",
        _ => "Document",
    };

    private Element BuildSideNavItem(AppNavigationItemViewModel item, bool isPaneOpen)
    {
        if (!_sideNavControls.TryGetValue(item.PageKey, out var control))
        {
            control = CreateSideNavItem(item.PageKey);
            control.Tapped += (_, _) => NavigateByTag(item.PageKey);
            _sideNavControls[item.PageKey] = control;
        }

        ApplySideNavItem(control, isPaneOpen ? item.Title : string.Empty, item.Symbol, item.IsSelected, item.ShowUnread);
        return new XamlHostElement(() => control, _ => { }).WithKey("sidenav-" + item.PageKey);
    }

    private Element BuildStaticNavItem(
        string title,
        FluentIcons.Common.Symbol symbol,
        string tag,
        bool isSelected,
        bool isPaneOpen)
    {
        if (!_sideNavControls.TryGetValue(tag, out var control))
        {
            control = CreateSideNavItem(tag);
            control.Tapped += (_, _) => NavigateByTag(tag);
            _sideNavControls[tag] = control;
        }

        ApplySideNavItem(control, isPaneOpen ? title : string.Empty, symbol, isSelected, false);
        return new XamlHostElement(() => control, _ => { }).WithKey("sidenav-" + tag);
    }

    private static AppNavigationViewItem CreateSideNavItem(string tag)
        => new()
        {
            // 内核模板：两个 SymbolIcon（Regular Opacity=1 / Filled Opacity=0），
            // 都挂 MainNavIconImplicitOffsetAnimation（Offset 0.4s + Opacity 0.2s）.
            Style = (Style)Application.Current.Resources["MainNavigationViewItemStyle"],
            SelectsOnInvoked = false,
            Tag = tag,
        };


    private static void HideDescendantTexts(DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock textBlock)
            {
                textBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                HideDescendantTexts(child);
            }
        }
    }
    private static void ApplySideNavItem(
        AppNavigationViewItem control,
        string content,
        FluentIcons.Common.Symbol symbol,
        bool isSelected,
        bool showUnread)
    {
        control.Content = content;
        control.Symbol = symbol;
        control.ShowUnread = showUnread;

        // IsSelected 变化会切模板的 Selected 视觉状态：DeselectedIcon.Opacity 1 -> 0、
        // SelectedIcon.Opacity 0 -> 1，两个都带 OpacityAnimation 0.2s，就是交叉淡入.
        // 程序化切页（不是点导航项）也走这里，所以同样会触发动画.
        control.IsSelected = isSelected;
    }

    private void NavigateToSettings()
    {
        var item = _viewModel.FooterItems.FirstOrDefault(entry => entry.PageKey.Contains("SettingsPage"));
        item?.NavigateCommand.Execute(default);
    }

    private void NavigateByTag(string tag)
    {
        Serilog.Log.Information("导航项被点击：{Tag}", tag);

        if (tag == SearchRailTag)
        {
            // 点窄 rail 上的搜索图标：先展开侧栏，再把焦点送进搜索框.
            _openPaneAction?.Invoke();
            _ = Task.Run(async () =>
            {
                await Task.Delay(200).ConfigureAwait(false);
                _searchBox.DispatcherQueue?.TryEnqueue(() => _searchBox.Focus(FocusState.Programmatic));
            });
            return;
        }

        var item = _viewModel.MenuItems.Concat(_viewModel.FooterItems)
            .FirstOrDefault(entry => entry.PageKey == tag);
        item?.NavigateCommand.Execute(default);
    }
}


