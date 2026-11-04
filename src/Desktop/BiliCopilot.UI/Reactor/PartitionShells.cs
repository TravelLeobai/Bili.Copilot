// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Article;
using BiliCopilot.UI.Controls.Pgc;
using BiliCopilot.UI.Controls.Popular;
using BiliCopilot.UI.Controls.VideoPartition;
using BiliCopilot.UI.ViewModels;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Richasy.WinUIKernel.Share.Base;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 分区类页面的通用外壳：56 高顶栏 + 1px 分割线 + 内容，左侧导航列可用分隔条拖动.
/// 顶栏高度与动态、消息、设置页保持一致.
/// </summary>
public abstract class PartitionShellBase : Component
{
    private readonly LayoutPageViewModelBase _viewModel;
    private readonly ColumnSplitter _splitter = new();
    private bool _hooked;

    /// <summary>
    /// Initializes a new instance of the <see cref="PartitionShellBase"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    protected PartitionShellBase(LayoutPageViewModelBase viewModel)
    {
        _viewModel = viewModel;
        _splitter.MinColumnWidth = 180;
        _splitter.PointerReleased += (_, _) => _viewModel.NavColumnWidth = _splitter.ColumnWidth;
    }

    /// <summary>
    /// 页面视图模型.
    /// </summary>
    protected LayoutPageViewModelBase ViewModel => _viewModel;

    /// <summary>
    /// 渲染左中右三列布局.
    /// </summary>
    /// <param name="sideHeader">左列顶栏控件.</param>
    /// <param name="sideBody">左列内容控件.</param>
    /// <param name="mainHeader">右列顶栏控件.</param>
    /// <param name="mainBody">右列内容控件.</param>
    protected Element BuildLayout(
        FrameworkElement sideHeader,
        FrameworkElement sideBody,
        FrameworkElement mainHeader,
        FrameworkElement mainBody)
    {
        if (!_hooked)
        {
            _hooked = true;
            _splitter.RegisterPropertyChangedCallback(
                ColumnSplitter.IsHideProperty,
                (_, _) => _viewModel.IsNavColumnManualHide = _splitter.IsHide);
        }

        var navVisible = !_viewModel.IsNavColumnManualHide;
        var width = _viewModel.NavColumnWidth;
        _splitter.ColumnWidth = navVisible ? width : 0;
        _splitter.IsHide = !navVisible;

        var sideHeaderHost = navVisible
            ? Host(sideHeader).Grid(row: 0, column: 0).Margin(0, 0, -6, 0)
            : (Element)TextBlock(string.Empty);

        var sideBodyHost = navVisible
            ? Host(sideBody).Grid(row: 2, column: 0).Margin(0, 0, -6, 0)
            : (Element)TextBlock(string.Empty);

        return Grid(
            [GridSize.Px(navVisible ? width : 0), GridSize.Auto, GridSize.Star()],
            [GridSize.Px(56), GridSize.Px(1), GridSize.Star()],
            sideHeaderHost,
            Host(mainHeader).Grid(row: 0, column: 2).Margin(-6, 0, 0, 0),
            ViewLaterShell.Divider().Grid(row: 1, column: 0, columnSpan: 3),
            sideBodyHost,
            Host(mainBody).Grid(row: 2, column: 2).Margin(-6, 0, 0, 0),
            Host(_splitter).Grid(row: 0, rowSpan: 3, column: 1));
    }

    /// <summary>
    /// 顶栏标题文字，样式与动态、消息、设置页一致.
    /// </summary>
    /// <param name="text">标题.</param>
    protected static Element Title(string text)
        => TextBlock(text)
            .FontSize(18)
            .FontWeight(FontWeights.SemiBold)
            .VAlign(VerticalAlignment.Center)
            .Margin(16, 0, -6, 0);

    private Element Host(FrameworkElement control)
        => ViewLaterShell.Host(control, _ => { }, _viewModel);
}

/// <summary>
/// 流行页外壳.
/// </summary>
public sealed class PopularShell : PartitionShellBase
{
    private readonly PopularPageViewModel _vm;
    private readonly PopularSideHeader _sideHeader = new();
    private readonly PopularSideBody _sideBody = new();
    private readonly PopularMainHeader _mainHeader = new();
    private readonly PopularMainBody _mainBody = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PopularShell"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    public PopularShell(PopularPageViewModel viewModel)
        : base(viewModel)
    {
        _vm = viewModel;
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        UseObservableTree(_vm);
        return BuildLayout(_sideHeader, _sideBody, _mainHeader, _mainBody);
    }
}

/// <summary>
/// 视频分区页外壳.
/// </summary>
public sealed class VideoPartitionShell : PartitionShellBase
{
    private readonly VideoPartitionPageViewModel _vm;
    private readonly VideoPartitionSideHeader _sideHeader = new();
    private readonly VideoPartitionSideBody _sideBody = new();
    private readonly VideoPartitionMainHeader _mainHeader = new();
    private readonly VideoPartitionMainBody _mainBody = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPartitionShell"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    public VideoPartitionShell(VideoPartitionPageViewModel viewModel)
        : base(viewModel)
    {
        _vm = viewModel;
        SyncPartition();
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        UseObservableTree(_vm);
        SyncPartition();
        return BuildLayout(_sideHeader, _sideBody, _mainHeader, _mainBody);
    }

    private void SyncPartition()
    {
        _mainHeader.ViewModel = _vm.SelectedPartition;
        _mainBody.ViewModel = _vm.SelectedPartition;
    }
}

/// <summary>
/// 文章分区页外壳.
/// </summary>
public sealed class ArticlePartitionShell : PartitionShellBase
{
    private readonly ArticlePartitionPageViewModel _vm;
    private readonly TextBlock _sideHeader = new() { Text = "文章" };
    private readonly ArticlePartitionSideBody _sideBody = new();
    private readonly ArticlePartitionMainHeader _mainHeader = new();
    private readonly ArticlePartitionMainBody _mainBody = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ArticlePartitionShell"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    public ArticlePartitionShell(ArticlePartitionPageViewModel viewModel)
        : base(viewModel)
    {
        _vm = viewModel;
        _sideHeader.FontSize = 18;
        _sideHeader.FontWeight = FontWeights.SemiBold;
        _sideHeader.VerticalAlignment = VerticalAlignment.Center;
        SyncPartition();
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        UseObservableTree(_vm);
        SyncPartition();
        return BuildLayout(_sideHeader, _sideBody, _mainHeader, _mainBody);
    }

    private void SyncPartition()
    {
        _mainHeader.ViewModel = _vm.SelectedPartition;
        _mainBody.ViewModel = _vm.SelectedPartition;
    }
}

/// <summary>
/// 影视页外壳.
/// </summary>
public sealed class CinemaShell : PartitionShellBase
{
    private readonly CinemaPageViewModel _vm;
    private readonly CinemaSideHeader _sideHeader = new();
    private readonly CinemaMainHeader _mainHeader = new();
    private readonly EntertainmentIndexSideControl _sideBody = new();
    private readonly EntertainmentIndexMainControl _mainBody = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CinemaShell"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    public CinemaShell(CinemaPageViewModel viewModel)
        : base(viewModel)
    {
        _vm = viewModel;
        SyncSection();
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        UseObservableTree(_vm);
        SyncSection();
        return BuildLayout(_sideHeader, _sideBody, _mainHeader, _mainBody);
    }

    private void SyncSection()
    {
        _sideBody.ViewModel = _vm.SelectedSection;
        _mainBody.ViewModel = _vm.SelectedSection;
    }
}

/// <summary>
/// 动漫页外壳：时间表与索引两种内容按所选分区切换.
/// </summary>
public sealed class AnimeShell : PartitionShellBase
{
    private readonly AnimePageViewModel _vm;
    private readonly AnimeSideHeader _sideHeader = new();
    private readonly AnimeMainHeader _mainHeader = new();
    private readonly AnimeTimelineSideControl _timelineSide = new();
    private readonly AnimeTimelineMainControl _timelineMain = new();
    private readonly EntertainmentIndexSideControl _indexSide = new();
    private readonly EntertainmentIndexMainControl _indexMain = new();
    private readonly Grid _sideHost = new();
    private readonly Grid _mainHost = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeShell"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    public AnimeShell(AnimePageViewModel viewModel)
        : base(viewModel)
    {
        _vm = viewModel;
        _sideHost.Children.Add(_timelineSide);
        _sideHost.Children.Add(_indexSide);
        _mainHost.Children.Add(_timelineMain);
        _mainHost.Children.Add(_indexMain);
        SyncSection();
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        UseObservableTree(_vm);
        SyncSection();
        return BuildLayout(_sideHeader, _sideHost, _mainHeader, _mainHost);
    }

    private void SyncSection()
    {
        var section = _vm.SelectedSection;
        var isTimeline = section is AnimeTimelineViewModel;
        _timelineSide.ViewModel = section as AnimeTimelineViewModel;
        _timelineMain.ViewModel = section as AnimeTimelineViewModel;
        _indexSide.ViewModel = section as EntertainmentIndexViewModel;
        _indexMain.ViewModel = section as EntertainmentIndexViewModel;
        _timelineSide.Visibility = isTimeline ? Visibility.Visible : Visibility.Collapsed;
        _timelineMain.Visibility = isTimeline ? Visibility.Visible : Visibility.Collapsed;
        _indexSide.Visibility = isTimeline ? Visibility.Collapsed : Visibility.Visible;
        _indexMain.Visibility = isTimeline ? Visibility.Collapsed : Visibility.Visible;
    }
}
