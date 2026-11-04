// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Favorites;
using BiliCopilot.UI.Controls.History;
using BiliCopilot.UI.Controls.LivePartition;
using BiliCopilot.UI.Controls.ViewLater;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
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
/// 直播分区页面外壳.
/// 主分栏 + 次分栏 + 内容，标题栏高度与原页面保持一致.
/// </summary>
public sealed class LivePartitionShell : Component
{
    private readonly LivePartitionPageViewModel _viewModel;
    private readonly LivePartitionMainSideBody _mainSideBody = new();
    private readonly LivePartitionSubSideBody _subSideBody = new();
    private readonly LivePartitionMainHeader _mainHeader = new();
    private readonly LiveRecommendMainBody _recommendBody = new();
    private readonly LiveSubPartitionMainBody _subBody = new();
    private readonly Richasy.WinUIKernel.Share.Base.ColumnSplitter _mainSplitter;
    private readonly Richasy.WinUIKernel.Share.Base.ColumnSplitter _subSplitter;

    /// <summary>
    /// Initializes a new instance of the <see cref="LivePartitionShell"/> class.
    /// </summary>
    public LivePartitionShell(LivePartitionPageViewModel viewModel)
    {
        _viewModel = viewModel;

        _mainSplitter = new Richasy.WinUIKernel.Share.Base.ColumnSplitter();
        _mainSplitter.ColumnWidth = viewModel.NavColumnWidth;
        _mainSplitter.IsHide = viewModel.IsNavColumnManualHide;
        _mainSplitter.PointerReleased += (s, _) =>
            viewModel.NavColumnWidth = ((Richasy.WinUIKernel.Share.Base.ColumnSplitter)s).ColumnWidth;

        _subSplitter = new Richasy.WinUIKernel.Share.Base.ColumnSplitter();
        _subSplitter.ColumnWidth = viewModel.SubNavColumnWidth;
        _subSplitter.IsHide = viewModel.IsNavColumnManualHide;
        _subSplitter.PointerReleased += (s, _) =>
            viewModel.SubNavColumnWidth = ((Richasy.WinUIKernel.Share.Base.ColumnSplitter)s).ColumnWidth;
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_viewModel);
        var navVisible = !vm.IsNavColumnManualHide;
        var subVisible = vm.IsSubNavEnabled;
        var navWidth = vm.NavColumnWidth;
        var subWidth = vm.SubNavColumnWidth;

        var title = navVisible
            ? TextBlock("直播").FontSize(14).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .VAlign(VerticalAlignment.Center)
                .Margin(16, 0, -6, 0)
            : (Element)TextBlock(string.Empty);

        var subTitle = subVisible
            ? TextBlock(vm.SelectedMainSection?.Title ?? string.Empty).FontSize(14)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .VAlign(VerticalAlignment.Center)
                .Margin(10, 0, -6, 0)
            : (Element)TextBlock(string.Empty);

        var mainSide = navVisible
            ? ViewLaterShell.Host(_mainSideBody, control => control.ViewModel = vm, vm).Grid(row: 2, column: 0).Margin(0, 0, -6, 0)
            : (Element)TextBlock(string.Empty);

        var subSide = subVisible
            ? ViewLaterShell.Host(_subSideBody, control => control.ViewModel = vm, vm).Grid(row: 2, column: 2).Margin(-6, 0, -6, 0)
            : (Element)TextBlock(string.Empty);

        Element body = vm.SelectedDetail is null
            ? ViewLaterShell.Host(_recommendBody, control => control.ViewModel = vm, vm).Grid(row: 2, column: 4).Margin(-6, 0, 0, 0)
            : ViewLaterShell.Host(_subBody, control => control.ViewModel = vm.SelectedDetail, vm).Grid(row: 2, column: 4).Margin(-6, 0, 0, 0);

        return Grid(
            [
                GridSize.Px(navVisible ? navWidth : 0),
                GridSize.Auto,
                GridSize.Px(subVisible ? subWidth : 0),
                GridSize.Auto,
                GridSize.Star(),
            ],
            [GridSize.Px(56), GridSize.Px(1), GridSize.Star()],
            title.Grid(row: 0, column: 0),
            subTitle.Grid(row: 0, column: 2),
            ViewLaterShell.Divider().Grid(row: 1, column: 0, columnSpan: 5),
            mainSide,
            subSide,
            ViewLaterShell.Host(_mainHeader, control => control.ViewModel = vm, vm).Grid(row: 0, column: 4).Margin(-6, 0, 0, 0),
            body,
            ViewLaterShell.Host(_mainSplitter, _ => { }, vm).Grid(row: 0, rowSpan: 3, column: 1),
            subVisible
                ? ViewLaterShell.Host(_subSplitter, _ => { }, vm).Grid(row: 0, rowSpan: 3, column: 3)
                : TextBlock(string.Empty));
    }


}