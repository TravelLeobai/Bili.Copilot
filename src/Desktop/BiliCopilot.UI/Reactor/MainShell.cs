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
    private readonly HeaderAccountControl _accountControl = new();
    private readonly Button _paneToggleButton = CreateFlatIconButton("\uE700", "切换导航栏");
    private readonly Button _backButton = CreateFlatIconButton("\uE72B", "返回");
    private readonly Button _favoriteButton = CreateFlatIconButton("\uE734", "我的收藏");
    private readonly Button _viewLaterButton = CreateFlatIconButton("\uE823", "稍后再看");
    private readonly Button _historyButton = CreateFlatIconButton("\uE81C", "历史记录");

    private Action? _togglePaneAction;

    private NavigationView? _navView;

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
        _paneToggleButton.Click += (_, _) => _togglePaneAction?.Invoke();
        _favoriteButton.Click += (_, _) => _viewModel.NavigateToOver(typeof(FavoritesPage), default);
        _viewLaterButton.Click += (_, _) => _viewModel.NavigateToOver(typeof(ViewLaterPage), default);
        _historyButton.Click += (_, _) => _viewModel.NavigateToOver(typeof(HistoryPage), default);

        // 启动时清一次背景缓存残留（旧版 ThemeCache、没被索引引用的孤儿目录）.
        try
        {
            Services.BackgroundLibrary.CleanupOrphanCache();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "清理背景缓存失败");
        }
    }

    /// <summary>
    /// 标题栏元素，供窗口设置拖拽区域.
    /// </summary>
    public FrameworkElement? TitleBarElement => _titleBarRef?.Current;

    /// <inheritdoc/>
    public override Element Render()
    {
        var (isPaneOpen, setIsPaneOpen) = UseState(true);
        _togglePaneAction = () =>
        {
            // Set(...) 是 Reactor 唯一能摸到 XAML NavigationView 的入口，
            // 拿到真实控件后直接翻 IsPaneOpen，再到 hook 里同步一下.
            if (_navView is { } navView)
            {
                navView.IsPaneOpen = !navView.IsPaneOpen;
                setIsPaneOpen(navView.IsPaneOpen);
            }
            else
            {
                setIsPaneOpen(!isPaneOpen);
            }
        };
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

        // 收藏 / 稍后再看 / 历史记录 受「顶部导航栏可见性」开关控制.
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

// 头像只留图，不要昵称；和上面的图标一样居中.
        Element accountRow = new XamlHostElement(() => _accountControl, control =>
            {
                control.MinWidth = 0;
                control.Width = 32;
                control.Height = 32;
                control.HorizontalAlignment = HorizontalAlignment.Center;
                control.VerticalAlignment = VerticalAlignment.Center;
                control.Loaded += (_, _) => HideDescendantTexts(control);
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
            .Margin(0, 12, 0, 0);

// 侧栏项改成内核 AppNavigationViewItem：模板里自带 Regular/Filled 两个
        // SymbolIcon 和 200ms 透明度隐式动画，选中/悬停/按下都由模板的 VisualState 管.
        // 菜单（动态/直播等）留在上面，收藏/稍后再看/历史/消息/设置全部进 footer.
        Element sideNav = VStack(2, sideMenuItems).Padding(8, 8, 8, 0);

        Element paneFooterContent = VStack(
            2,
            [.. sideFooterItems, accountPanel])
            .Padding(8, 0, 8, 8);

        var navigation = (NavigationView([]) with
        {
            Content = new XamlHostElement(() => _contentHost, _ => { }),
            FooterMenuItems = [],
            IsBackEnabled = viewModel.IsOverlayOpen,
            OnBackRequested = () => viewModel.Back(),
            OnItemInvoked = tag => NavigateByTag(tag),
            OnSettingsSelected = () => NavigateToSettings(),
            IsPaneOpen = isPaneOpen,
            IsPaneToggleButtonVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            // 原版是窄栏：图标在上、文字在下。LeftCompact 才会走模板的竖向排版.
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
            OpenPaneLength = 240,
            CompactPaneLength = 72,
            PaneCustomContent = sideNav,
        })
            .PaneFooter(paneFooterContent)
            .PaneToggleButtonVisible(false)
            .Background("Transparent")
            // Set 是 Reactor 唯一能碰到 XAML NavigationView 的入口，折叠按钮靠它.
            // 自绘的折叠按钮删了；NavigationView 模板里那个内置的等控件加载后藏掉.
            .Set(nv =>
            {
                _navView = nv;
                nv.IsPaneToggleButtonVisible = false;
                // Reactor 分析器不给在组件里命令式绑事件，排一次队列等模板展开再找.
                nv.DispatcherQueue.TryEnqueue(() =>
                {
                    if (nv.FindName("TogglePaneButton") is FrameworkElement toggleButton)
                    {
                        toggleButton.Visibility = Visibility.Collapsed;
                    }
                });
            })
            .Grid(row: 1);

        var titleContent = HStack(
            4,
            viewModel.IsOverlayOpen
                ? new XamlHostElement(() => _backButton, _ => { })
                    .Size(40, 40)
                    // 纵向跟侧栏图标同一条竖线，横向跟顶栏文字同一条横线.
                    .Margin(17, 0, 0, 0)
                    .HAlign(HorizontalAlignment.Left)
                    .VAlign(VerticalAlignment.Center)
                : (Element)TextBlock(string.Empty),
            // 搜索图标只在侧栏窄 rail 上（折叠态），标题栏这里留空.
            (Element)TextBlock(string.Empty),
            TextBlock("哔哩助理").SemiBold().FontSize(14).Margin(4, 0, 0, 0).VAlign(VerticalAlignment.Center))
            .VAlign(VerticalAlignment.Center)
            .HAlign(HorizontalAlignment.Left)
            .Grid(row: 0, column: 0);

        // 顶部导航栏那套按钮后来删掉了，这里不再渲染.
        Element navButtons = TextBlock(string.Empty);

        var titleBar = Grid(
            columns: [GridSize.Auto, GridSize.Star()],
            rows: [GridSize.Star()],
            HStack(
                4,
                titleContent,
                navButtons)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),
            HStack(0, TextBlock(string.Empty))
                .Padding(0, 0, 12, 0)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 1))
            .Height(48)
            .Ref(titleBarRef);

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            titleBar.Grid(row: 0),
            navigation);
    }

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
            // 模板自带内边距会把内容往右推，清掉让图标贴左.
            Padding = new Thickness(0),
            // 模板的 CornerRadius 取 ControlCornerRadius(4)，放大一点圆角才看得出来.
            CornerRadius = new CornerRadius(6),
            // 侧栏 72 宽、左右各留 8，项必须限死在 56，否则内容一长就撑出容器被裁.
            MaxWidth = 56,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = tag,
        };

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

// 收藏 / 稍后再看 / 历史记录 是 overlay 页面，PageKey 里没有这几种 tag，
        // 之前直接去 MenuItems 里找，找不到就静默返回，所以点了没反应.
        switch (tag)
        {
            case "__favorites__":
                _viewModel.NavigateToOver(typeof(BiliCopilot.UI.Pages.Overlay.FavoritesPage));
                return;
            case "__view_later__":
                _viewModel.NavigateToOver(typeof(BiliCopilot.UI.Pages.Overlay.ViewLaterPage));
                return;
            case "__history__":
                _viewModel.NavigateToOver(typeof(BiliCopilot.UI.Pages.Overlay.HistoryPage));
                return;
        }

        var item = _viewModel.MenuItems.Concat(_viewModel.FooterItems)
            .FirstOrDefault(entry => entry.PageKey == tag);
        item?.NavigateCommand.Execute(default);
    }
}















