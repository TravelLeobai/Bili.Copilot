// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 视频合集（UGC 合集）区域，内容由 Reactor 渲染.
/// </summary>
public sealed partial class VideoSeasonSection : VideoSeasonSectionBase
{
    private ReactorHostControl? _reactorHost;
    private VideoPlayerSeasonSectionDetailViewModel? _mountedViewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoSeasonSection"/> class.
    /// </summary>
    public VideoSeasonSection() => InitializeComponent();

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
    protected override void OnViewModelChanged(VideoPlayerSeasonSectionDetailViewModel? oldValue, VideoPlayerSeasonSectionDetailViewModel? newValue)
        => MountPanel(newValue);

    private void MountPanel(VideoPlayerSeasonSectionDetailViewModel? viewModel)
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
        _reactorHost.Mount(new VideoSeasonPanel(viewModel));
    }
}

/// <summary>
/// 视频合集区域基类.
/// </summary>
public abstract class VideoSeasonSectionBase : LayoutUserControlBase<VideoPlayerSeasonSectionDetailViewModel>
{
}