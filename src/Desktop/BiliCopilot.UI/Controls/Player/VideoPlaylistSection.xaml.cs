// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 播放列表面板区域，内容由 Reactor 渲染.
/// </summary>
public sealed partial class VideoPlaylistSection : VideoPlaylistSectionBase
{
    private ReactorHostControl? _reactorHost;
    private VideoPlayerPlaylistSectionDetailViewModel? _mountedViewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPlaylistSection"/> class.
    /// </summary>
    public VideoPlaylistSection() => InitializeComponent();

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
    protected override void OnViewModelChanged(VideoPlayerPlaylistSectionDetailViewModel? oldValue, VideoPlayerPlaylistSectionDetailViewModel? newValue)
        => MountPanel(newValue);

    private void MountPanel(VideoPlayerPlaylistSectionDetailViewModel? viewModel)
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
        _reactorHost.Mount(new VideoPlaylistPanel(viewModel));
    }
}

/// <summary>
/// 播放列表面板基类.
/// </summary>
public abstract class VideoPlaylistSectionBase : LayoutUserControlBase<VideoPlayerPlaylistSectionDetailViewModel>
{
}