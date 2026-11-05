// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Comment;
using BiliCopilot.UI.Controls.Moment;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 使用 Reactor 重写的动态页面外壳.
/// 顶部分段与内容切换由 Reactor 驱动，动态列表复用原有详情控件.
/// </summary>
public sealed class MomentShell : Component
{
    private readonly MomentPageViewModel _viewModel;
    private readonly MomentHeader _header = new();
    private readonly VideoMomentSectionDetailControl _videoControl = new();
    private readonly ComprehensiveMomentSectionDetailControl _comprehensiveControl = new();
    private readonly CommentOverlayPanel _commentPanel = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MomentShell"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    public MomentShell(MomentPageViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_viewModel);
        var section = vm.SelectedSection;
        var isVideo = section is VideoMomentSectionDetailViewModel;
        if (section is VideoMomentSectionDetailViewModel videoSection)
        {
            _videoControl.ViewModel = videoSection;
        }
        else if (section is ComprehensiveMomentSectionDetailViewModel comprehensiveSection)
        {
            _comprehensiveControl.ViewModel = comprehensiveSection;
        }

        _commentPanel.ViewModel = vm.CommentModule;
        _commentPanel.IsPanelOpened = vm.IsCommentsOpened;

        // 两个分区的控件都常驻，切 tab 只切控件的 Visibility.
        // 之前是在同一个位置换 TypeKey，Reactor 会重建宿主元素，挂在上面的控件实例
        // 跟着被丢掉，切回综合时整片内容（包括顶部 UP 主栏）都是空白.
        _videoControl.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        _comprehensiveControl.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;

        var children = new List<Element>
        {
            new XamlHostElement(() => _header, _ => { })
                .Grid(row: 0)
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Center),
            new XamlHostElement(() => _videoControl, _ => { })
                .Grid(row: 1)
                .WithKey("moment-video"),
            new XamlHostElement(() => _comprehensiveControl, _ => { })
                .Grid(row: 1)
                .WithKey("moment-comprehensive"),
        };

        if (vm.IsCommentsOpened)
        {
            children.Add(new XamlHostElement(() => _commentPanel, _ => { })
                .Grid(row: 0, rowSpan: 2)
                .WithKey("moment-comments"));
        }

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Px(56), GridSize.Star()],
            children.ToArray());
    }
}
