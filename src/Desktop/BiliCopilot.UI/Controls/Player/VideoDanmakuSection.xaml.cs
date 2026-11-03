// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 视频播放页弹幕列表区域.
/// </summary>
public sealed partial class VideoDanmakuSection : VideoDanmakuSectionBase
{
    private ReactorHostControl? _reactorHost;
    private VideoPlayerDanmakuSectionDetailViewModel? _mountedViewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoDanmakuSection"/> class.
    /// </summary>
    public VideoDanmakuSection() => InitializeComponent();

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
    protected override void OnViewModelChanged(VideoPlayerDanmakuSectionDetailViewModel? oldValue, VideoPlayerDanmakuSectionDetailViewModel? newValue)
        => MountPanel(newValue);

    private void MountPanel(VideoPlayerDanmakuSectionDetailViewModel? viewModel)
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
        _reactorHost.Mount(new DanmakuPanel(viewModel));
    }
}

/// <summary>
/// 视频播放页弹幕列表区域基类.
/// </summary>
public abstract class VideoDanmakuSectionBase : LayoutUserControlBase<VideoPlayerDanmakuSectionDetailViewModel>
{
}
