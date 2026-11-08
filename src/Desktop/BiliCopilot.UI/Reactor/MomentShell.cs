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
    // 直播段的控件延迟创建：只有真的切到"直播"那一刻才实例化，
    // 万一这一段有隐藏问题，也不会拖累动态页其它分段.
    private LiveMomentSectionDetailControl? _liveControl;
    private readonly CommentOverlayPanel _commentPanel = new();
    private int _commentsOpenVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="MomentShell"/> class.
    /// </summary>
    /// <param name="viewModel">页面视图模型.</param>
    public MomentShell(MomentPageViewModel viewModel)
    {
        _viewModel = viewModel;

        // 用当前版本号做基线：只响应「之后」的打开请求，
        // 否则首次渲染就会当成发生了一次打开，面板一进来就盖在动态页上.
        _commentsOpenVersion = viewModel.CommentsOpenVersion;
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_viewModel);
        var section = vm.SelectedSection;
        var isVideo = section is VideoMomentSectionDetailViewModel;
        var isLive = section is LiveMomentSectionDetailViewModel;
        if (section is VideoMomentSectionDetailViewModel videoSection)
        {
            _videoControl.ViewModel = videoSection;
        }
        else if (section is ComprehensiveMomentSectionDetailViewModel comprehensiveSection)
        {
            _comprehensiveControl.ViewModel = comprehensiveSection;
        }
        else if (section is LiveMomentSectionDetailViewModel liveSection)
        {
            // 直播段控件延迟创建：只在真正切到"直播"时才实例化.
            _liveControl ??= new LiveMomentSectionDetailControl();
            _liveControl.ViewModel = liveSection;
        }

        _commentPanel.ViewModel = vm.CommentModule;

        // 只在「用户又请求打开」时把面板打开；收起由面板自己处理（点遮罩）.
        // 这里绝不能无条件写 IsPanelOpened = vm 的值：用户刚收起的面板会被下一帧推回打开，
        // 动态列表一滚动就复现「旧评论列表自己弹出来」.
        if (_commentsOpenVersion != vm.CommentsOpenVersion)
        {
            _commentsOpenVersion = vm.CommentsOpenVersion;
            _commentPanel.IsPanelOpened = true;
        }

        // 两个分区的控件都常驻，切 tab 只切控件的 Visibility.
        // 之前是在同一个位置换 TypeKey，Reactor 会重建宿主元素，挂在上面的控件实例
        // 跟着被丢掉，切回综合时整片内容（包括顶部 UP 主栏）都是空白.
        _videoControl.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        _comprehensiveControl.Visibility = !isVideo && !isLive ? Visibility.Visible : Visibility.Collapsed;
        if (_liveControl is not null)
        {
            _liveControl.Visibility = isLive ? Visibility.Visible : Visibility.Collapsed;
        }

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
            _liveControl is null
                ? TextBlock(string.Empty)
                : new XamlHostElement(() => _liveControl, _ => { })
                    .Grid(row: 1)
                    .WithKey("moment-live"),

            // 评论面板常驻，开关只切它自己的 Visibility（绑 IsPanelOpened）.
            // 之前是按 IsCommentsOpened 决定要不要把它加进树：每次开关就是挂载/卸载一次，
            // 卸载时 OnControlUnloaded 会把里面挂着的 Reactor 宿主整个 dispose 掉，
            // 于是每关一次面板都要重建评论组件，列表跟着从头来过；
            // 在点击事件里同步触发还会把事件源自己拆掉，直接把进程搞崩.
            new XamlHostElement(() => _commentPanel, _ => { })
                .Grid(row: 0, rowSpan: 2)
                .WithKey("moment-comments"),
        };

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Px(56), GridSize.Star()],
            children.ToArray());
    }
}


