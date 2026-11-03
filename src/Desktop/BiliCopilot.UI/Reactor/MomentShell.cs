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

        var content = new XamlHostElement(
            () => isVideo ? _videoControl : _comprehensiveControl,
            _ => { })
        { TypeKey = isVideo ? "moment-video" : "moment-comprehensive" };

        var children = new List<Element>
        {
            new XamlHostElement(() => _header, _ => { })
                .Grid(row: 0)
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Center),
            content.Grid(row: 1),
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
