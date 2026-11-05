// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls;
using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
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
    private readonly Button _paneToggleButton = CreateFlatIconButton("\uE700", "切换导航栏");
    private readonly Button _compactSearchButton = CreateFlatIconButton("\uE721", "搜索");

    private readonly Button _railSearchButton = CreateFlatIconButton("\uE721", "搜索");
    private readonly Button _backButton = CreateFlatIconButton("\uE72B", "返回");
    private readonly Button _favoriteButton = CreateFlatIconButton("\uE734", "我的收藏");
    private readonly Button _viewLaterButton = CreateFlatIconButton("\uE823", "稍后再看");
    private readonly Button _historyButton = CreateFlatIconButton("\uE81C", "历史记录");
    private const string SearchRailTag = "__rail_search__";

    private Action? _togglePaneAction;

    private NavigationView? _navView;
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

        var menu = viewModel.MenuItems
            .Where(item => item.IsVisible)
            .Select(item => new NavigationViewItemData(item.Title, GetSymbolName(item.PageKey), item.PageKey))
            .ToList();

        var menuItems = menu.ToArray();
        var footer = viewModel.FooterItems
            .Where(item => item.IsVisible && !item.PageKey.Contains("SettingsPage"))
            .Select(item => new NavigationViewItemData(item.Title, GetSymbolName(item.PageKey), item.PageKey))
            .ToArray();

        Element accountRow = HStack(
            9.7,
            new XamlHostElement(() => _accountControl, _ => { }),
            isPaneOpen
                ? TextBlock(account.MyProfile?.User?.Name ?? string.Empty)
                    .FontSize(14)
                    .VAlign(VerticalAlignment.Center)
                : (Element)TextBlock(string.Empty))
            .Padding(4.7, 0, 12, 0)
            .Height(36);

        Element accountPanel = Button(accountRow, () => _accountControl.ShowAccountFlyout())
            .AutomationName(account.MyProfile?.User?.Name ?? "我的")
            .SubtleButton()
            .Padding(0)
            .Height(36)
            .Margin(4, 0, 4, 0)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center);

        // 侧栏顶部：展开时是完整搜索框，收起时只剩一个搜索图标.
        Element paneHeader = isPaneOpen
            ? new XamlHostElement(() => _searchBox, _ => { })
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Center)
                .Margin(12, 6, 8, 8)
            : (Element)TextBlock(string.Empty);

        var navigation = (NavigationView(menuItems) with
        {
            Content = new XamlHostElement(() => _contentHost, _ => { }),
            FooterMenuItems = footer,
            IsBackEnabled = viewModel.IsOverlayOpen,
            OnBackRequested = () => viewModel.Back(),
            OnItemInvoked = tag => NavigateByTag(tag),
            OnSettingsSelected = () => NavigateToSettings(),
            IsPaneOpen = isPaneOpen,
            IsPaneToggleButtonVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            PaneHeader = paneHeader,
        })
            .PaneFooter(accountPanel)
            .Background("Transparent")
            // Set 是 Reactor 唯一能碰到 XAML NavigationView 的入口：
            // 存下控件给折叠按钮用，同时把当前页面的导航项设成选中 ——
            // 选中后图标和文字会走系统强调色，跟 f7a634e 那套着色是同一个效果.
            .Set(nv =>
            {
                _navView = nv;
                var selectedKey = viewModel.CurrentPageKey;

                if (selectedKey is null)
                {
                    return;
                }

                foreach (var candidate in nv.MenuItems.Concat(nv.FooterMenuItems).OfType<NavigationViewItem>())
                {
                    if (candidate.Tag as string == selectedKey)
                    {
                        nv.SelectedItem = candidate;

                        // 选中项显式染成强调色，跟 f7a634e 那套「选中变红」的观感对齐.
                        if (Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var accentBrush)
                            && accentBrush is Brush accent)
                        {
                            candidate.Foreground = accent;
                            if (candidate.Icon is Microsoft.UI.Xaml.Controls.IconElement iconElement)
                            {
                                iconElement.Foreground = accent;
                            }
                        }

                        break;
                    }
                }
            })
            .Grid(row: 1);

        var titleContent = HStack(
            4,
            viewModel.IsOverlayOpen
                ? new XamlHostElement(() => _backButton, _ => { })
                    .Size(40, 40)
                    .HAlign(HorizontalAlignment.Left)
                    .VAlign(VerticalAlignment.Center)
                : (Element)TextBlock(string.Empty),
            new XamlHostElement(() => _paneToggleButton, _ => { })
                .Size(40, 40)
                .Margin(3.5, 0, 0, 0)
                .HAlign(HorizontalAlignment.Left)
                .VAlign(VerticalAlignment.Center),
            // 搜索图标只在侧栏窄 rail 上（折叠态），标题栏这里留空.
            (Element)TextBlock(string.Empty),
            TextBlock("哔哩助理").SemiBold().FontSize(14).VAlign(VerticalAlignment.Center))
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

        // 折叠态：在左侧窄 rail 顶部叠一个真的搜索按钮（导航项点击在 rail 里不可靠）.
        Element railSearch = isPaneOpen
            ? (Element)TextBlock(string.Empty)
            : new XamlHostElement(() => _railSearchButton, _ => { })
                .Size(48, 36)
                .HAlign(HorizontalAlignment.Left)
                .VAlign(VerticalAlignment.Top)
                .Margin(0, 6, 0, 0)
                .Grid(row: 1);

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            titleBar.Grid(row: 0),
            navigation,
            railSearch);
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
