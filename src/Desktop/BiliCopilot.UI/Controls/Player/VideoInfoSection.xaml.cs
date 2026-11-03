// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 视频信息区域，内容由 Reactor 渲染（内部沿用原有信息与操作控件）.
/// </summary>
public sealed partial class VideoInfoSection : VideoInfoSectionBase
{
    private ReactorHostControl? _reactorHost;
    private VideoPlayerInfoSectionDetailViewModel? _mountedViewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoInfoSection"/> class.
    /// </summary>
    public VideoInfoSection() => InitializeComponent();

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
    protected override void OnViewModelChanged(VideoPlayerInfoSectionDetailViewModel? oldValue, VideoPlayerInfoSectionDetailViewModel? newValue)
        => MountPanel(newValue);

    private void MountPanel(VideoPlayerInfoSectionDetailViewModel? viewModel)
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
        _reactorHost.Mount(new VideoInfoPanel(viewModel));
    }
}

/// <summary>
/// 视频信息区域基类.
/// </summary>
public abstract class VideoInfoSectionBase : LayoutUserControlBase<VideoPlayerInfoSectionDetailViewModel>
{
}