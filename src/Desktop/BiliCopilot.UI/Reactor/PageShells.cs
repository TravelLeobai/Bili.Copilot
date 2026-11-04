// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Favorites;
using BiliCopilot.UI.Controls.History;
using BiliCopilot.UI.Controls.LivePartition;
using BiliCopilot.UI.Controls.ViewLater;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 稍后再看页面外壳.
/// 56 高顶栏 + 分割线 + 内容，和原 XAML 的三行布局一致.
/// </summary>
public sealed class ViewLaterShell : Component
{
    private readonly ViewLaterPageViewModel _viewModel;
    private readonly ViewLaterHeader _header = new();
    private readonly ViewLaterBody _body = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ViewLaterShell"/> class.
    /// </summary>
    public ViewLaterShell(ViewLaterPageViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        UseObservableTree(_viewModel);

        return Grid(
            [GridSize.Star()],
            [GridSize.Px(56), GridSize.Px(1), GridSize.Star()],
            Host(_header, control => control.ViewModel = _viewModel).Grid(row: 0),
            Divider().Grid(row: 1),
            Host(_body, control => control.ViewModel = _viewModel).Grid(row: 2));
    }

    internal static Element Host<T>(T control, Action<T> apply, object? state = null)
        where T : Microsoft.UI.Xaml.FrameworkElement
        => new XamlHostElement(
            () => control,
            _ => apply(control));

    internal static Element Divider()
        => Grid([GridSize.Star()], [GridSize.Px(1)])
            .Background(ResolveBrush("DividerStrokeColorDefaultBrush"));

    internal static Brush ResolveBrush(string name)
        => Application.Current.Resources.TryGetValue(name, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}

/// <summary>
/// 历史记录页面外壳.
/// </summary>
public sealed class HistoryShell : Component
{
    private readonly HistoryPageViewModel _viewModel;
    private readonly HistoryHeader _header = new();
    private readonly HistoryVideoSearchSection _searchSection = new();
    private readonly VideoHistorySection _videoSection = new();
    private readonly LiveHistorySection _liveSection = new();
    private readonly ArticleHistorySection _articleSection = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryShell"/> class.
    /// </summary>
    public HistoryShell(HistoryPageViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_viewModel);

        Element content;
        if (vm.IsSearchMode)
        {
            content = ViewLaterShell.Host(_searchSection, control => control.ViewModel = vm, vm);
        }
        else
        {
            content = vm.SelectedSection switch
            {
                LiveHistorySectionDetailViewModel live => ViewLaterShell.Host(_liveSection, c => c.ViewModel = live, vm),
                ArticleHistorySectionDetailViewModel article => ViewLaterShell.Host(_articleSection, c => c.ViewModel = article, vm),
                VideoHistorySectionDetailViewModel video => ViewLaterShell.Host(_videoSection, c => c.ViewModel = video, vm),
                _ => TextBlock(string.Empty),
            };
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Px(56), GridSize.Px(1), GridSize.Star()],
            ViewLaterShell.Host(_header, control => control.ViewModel = vm, vm).Grid(row: 0),
            ViewLaterShell.Divider().Grid(row: 1),
            content.Grid(row: 2));
    }
}

/// <summary>
/// 收藏页面外壳.
/// 左侧导航栏（可收起）+ 右侧内容，分栏拖拽仍交给原来的 ColumnSplitter.
/// </summary>
public sealed class FavoritesShell : Component
{
    private readonly FavoritesPageViewModel _viewModel;
    private readonly FavoritesPageSideHeader _sideHeader = new();
    private readonly FavoritesPageSideBody _sideBody = new();
    private readonly FavoritesPageMainHeader _mainHeader = new();
    private readonly FavoritesPageMainBody _mainBody = new();
    private readonly Richasy.WinUIKernel.Share.Base.ColumnSplitter _splitter;

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoritesShell"/> class.
    /// </summary>
    public FavoritesShell(FavoritesPageViewModel viewModel)
    {
        _viewModel = viewModel;
        _splitter = new Richasy.WinUIKernel.Share.Base.ColumnSplitter();
        // 传统 Binding 需要源实现 ICustomProperty，普通 ObservableObject 会抛 NotSupportedException，改成手动同步.
        _splitter.ColumnWidth = viewModel.NavColumnWidth;
        _splitter.IsHide = viewModel.IsNavColumnManualHide;
        // ColumnSplitter 没有宽度变更事件，拖完用指针释放把结果写回 VM.
        _splitter.PointerReleased += (s, _) =>
            viewModel.NavColumnWidth = ((Richasy.WinUIKernel.Share.Base.ColumnSplitter)s).ColumnWidth;
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_viewModel);
        var navVisible = !vm.IsNavColumnManualHide;
        var navWidth = vm.NavColumnWidth;

        var side = navVisible
            ? Grid(
                [GridSize.Star()],
                [GridSize.Px(56), GridSize.Px(1), GridSize.Star()],
                ViewLaterShell.Host(_sideHeader, control => control.ViewModel = vm, vm).Grid(row: 0).Margin(0, 0, -6, 0),
                ViewLaterShell.Divider().Grid(row: 1),
                ViewLaterShell.Host(_sideBody, control => control.ViewModel = vm, vm).Grid(row: 2).Margin(0, 0, -6, 0))
            : (Element)TextBlock(string.Empty);

        return Grid(
            [GridSize.Px(navVisible ? navWidth : 0), GridSize.Auto, GridSize.Star()],
            [GridSize.Star()],
            side.Grid(row: 0, column: 0),
            ViewLaterShell.Host(_splitter, _ => { }, vm).Grid(row: 0, column: 1),
            Grid(
                [GridSize.Star()],
                [GridSize.Px(56), GridSize.Px(1), GridSize.Star()],
                ViewLaterShell.Host(_mainHeader, control => control.ViewModel = vm, vm).Grid(row: 0).Margin(-6, 0, 0, 0),
                ViewLaterShell.Divider().Grid(row: 1),
                ViewLaterShell.Host(_mainBody, control => control.ViewModel = vm, vm).Grid(row: 2).Margin(-6, 0, 0, 0))
                .Grid(row: 0, column: 2));
    }


}

/// <summary>
/// 直播页外壳：顶栏（标题 + 筛选 + 刷新）、关注 UP 主条、瀑布流卡片.
/// 左边栏已经去掉，分区选择收进顶栏的筛选按钮.
/// </summary>
public sealed class LivePartitionShell : Component
{
    private readonly LivePartitionPageViewModel _viewModel;
    private readonly LiveRecommendMainBody _recommendBody = new();
    private readonly LiveSubPartitionMainBody _partitionBody = new();
    private readonly FollowedLiveBar _followedBar = new();
    private Flyout? _filterFlyout;
    private TextBlock? _filterLabel;
    private Button? _filterButton;

    /// <summary>
    /// Initializes a new instance of the <see cref="LivePartitionShell"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    public LivePartitionShell(LivePartitionPageViewModel viewModel)
    {
        _viewModel = viewModel;
        _followedBar.ViewModel = viewModel;
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_viewModel);
        var followVisible = !vm.IsFollowRoomsEmpty;
        var inPartition = vm.SelectedDetail is not null;


        // 控件 ViewModel 在这里直接写：Host 的 apply 只在元素挂载时跑一次，
        // 切换分区后元素被复用，靠 apply 永远拿不到新的 SelectedDetail.
        _recommendBody.ViewModel = vm;
        if (vm.SelectedDetail is { } detail)
        {
            _partitionBody.ViewModel = detail;
        }

        // 顶栏按钮文字和自动化名字都跟着当前分区走.
        var filterLabel = GetFilterLabel(vm);
        if (_filterLabel is not null && _filterLabel.Text != filterLabel)
        {
            _filterLabel.Text = filterLabel;
        }

        if (_filterButton is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_filterButton, filterLabel);
        }

        Element body = inPartition
            ? ViewLaterShell.Host(_partitionBody, _ => { }, vm).Grid(row: 3).Margin(16, 0, 16, 0).WithKey("live-partition")
            : ViewLaterShell.Host(_recommendBody, _ => { }, vm).Grid(row: 3).Margin(16, 0, 16, 0).WithKey("live-recommend");
        return Grid(
            [GridSize.Star()],
            [GridSize.Px(56), GridSize.Px(followVisible ? 76 : 0), GridSize.Px(1), GridSize.Star()],
            BuildHeader(vm).Grid(row: 0),
            followVisible
                ? ViewLaterShell.Host(_followedBar, control => control.ViewModel = vm, vm).Grid(row: 1)
                : TextBlock(string.Empty),
            ViewLaterShell.Divider().Grid(row: 2),
            body);
    }

    private Element BuildHeader(LivePartitionPageViewModel vm)
        => Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Star()],
            TextBlock("直播")
                .FontSize(18)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .VAlign(VerticalAlignment.Center)
                .Margin(16, 0, 0, 0)
                .Grid(row: 0, column: 0),
            HStack(
                8,
                BuildFilterButton(vm),
                BuildRefreshButton(vm))
                .Grid(row: 0, column: 1)
                .Margin(0, 0, 16, 0)
                .VAlign(VerticalAlignment.Center));

    private static Element BuildRefreshButton(LivePartitionPageViewModel vm)
        => new XamlHostElement(
            () =>
            {
                var button = CreateToolbarButton("刷新", FluentIcons.Common.Symbol.ArrowSync);
                button.Click += (_, _) => vm.RefreshCommand.Execute(default);
                return button;
            },
            _ => { })
            .WithKey("live-refresh");

    private static string GetFilterLabel(LivePartitionPageViewModel vm)
    {
        var section = vm.SelectedMainSection;
        return section is null || section.Data.Id == "_" ? "筛选" : section.Title;
    }

    private Element BuildFilterButton(LivePartitionPageViewModel vm)
        => new XamlHostElement(
            () =>
            {
                var button = CreateToolbarButton(GetFilterLabel(vm), FluentIcons.Common.Symbol.Filter);
                _filterButton = button;
                _filterLabel = (TextBlock)((StackPanel)button.Content).Children[1];
                button.Click += (_, _) => ShowFilterFlyout(vm, button);
                return button;
            },
            _ => { })
            .WithKey("live-filter");

    private static Button CreateToolbarButton(string text, FluentIcons.Common.Symbol symbol)
    {
        var icon = new FluentIcons.WinUI.SymbolIcon
        {
            Symbol = symbol,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ViewLaterShell.ResolveBrush("AccentTextFillColorPrimaryBrush"),
        };

        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(icon);
        panel.Children.Add(label);
        var button = new Button
        {
            Content = panel,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Content 是面板时自动化名字是空的，补一个给 UIA 用.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    private void ShowFilterFlyout(LivePartitionPageViewModel vm, FrameworkElement anchor)
    {
        var panel = new StackPanel { MinWidth = 200, Spacing = 2 };

        var all = vm.MainSections.FirstOrDefault(p => p.Data.Id == "_");
        panel.Children.Add(CreateSectionRow(vm, all, "全部直播"));

        // 「推荐的直播分区」只是分组标题，不是能筛的分区，跳过.
        var groupTitle = global::BiliCopilot.UI.Toolkits.ResourceToolkit.GetLocalizedString(
            global::BiliCopilot.UI.Models.Constants.StringNames.RecommendLivePartition);

        foreach (var section in vm.MainSections)
        {
            if (ReferenceEquals(section, all) || string.Equals(section.Title, groupTitle, StringComparison.Ordinal))
            {
                continue;
            }

            panel.Children.Add(CreateSectionRow(vm, section, section.Title));
        }

        _filterFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            },
        };

        _filterFlyout.ShowAt(anchor);
    }

    private Button CreateSectionRow(LivePartitionPageViewModel vm, PartitionViewModel? section, string title)
    {
        var isSelected = section is not null && section.Equals(vm.SelectedMainSection);

        // 选中的分区前面打勾，没选中的留同宽空位，文字不会左右跳.
        var check = new FluentIcons.WinUI.SymbolIcon
        {
            Symbol = FluentIcons.Common.Symbol.Checkmark,
            FontSize = 12,
            Width = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
        };

        var label = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(check);
        panel.Children.Add(label);

        var button = new Button
        {
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);

        button.Click += (_, _) =>
        {
            if (section is not null && !section.Equals(vm.SelectedMainSection) && vm.SelectMainSectionCommand.CanExecute(section))
            {
                vm.SelectMainSectionCommand.Execute(section);
            }

            _filterFlyout?.Hide();
        };

        return button;
    }
}
