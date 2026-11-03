// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 播放页侧边栏评论列表，内容由 Reactor 渲染.
/// </summary>
public sealed partial class PlayerCommentSection : PlayerCommentSectionBase
{
    private ReactorHostControl? _reactorHost;
    private CommentMainViewModel? _mountedViewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerCommentSection"/> class.
    /// </summary>
    public PlayerCommentSection() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnControlLoaded() => MountPanel(ViewModel);

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        _reactorHost?.Dispose();
        _reactorHost = null;
        _mountedViewModel = null;
        RootGrid.Children.Clear();
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(CommentMainViewModel? oldValue, CommentMainViewModel? newValue)
        => MountPanel(newValue);

    private void MountPanel(CommentMainViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(viewModel, _mountedViewModel))
        {
            return;
        }

        _reactorHost?.Dispose();
        RootGrid.Children.Clear();

        _mountedViewModel = viewModel;
        _reactorHost = new ReactorHostControl();
        RootGrid.Children.Add(_reactorHost);
        _reactorHost.Mount(new CommentPanel(viewModel));
    }
}

/// <summary>
/// 播放页侧边栏评论列表基类.
/// </summary>
public abstract class PlayerCommentSectionBase : LayoutUserControlBase<CommentMainViewModel>
{
}