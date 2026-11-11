// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
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

        // 性能探针：侧栏根元素的 Loaded 才算"真的挂上去了"，在这里收尾 Detail.Panel.Mount。
        // 探针关闭时不挂事件，零开销.
        if (PerfProbe.IsEnabled)
        {
            _reactorHost.Loaded += OnPanelLoaded;
        }

        RootGrid.Children.Add(_reactorHost);
        _reactorHost.Mount(new VideoInfoPanel(viewModel));

        // 性能探针：在挂载这一刻投一个低优先级回调，测"UI 线程跑到下一个空闲点"要多久，
        // 近似于挂载后的第一次布局/渲染.
        PerfProbe.QueueDetailPanelFirstRenderProbe();
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (_reactorHost is not null)
        {
            _reactorHost.Loaded -= OnPanelLoaded;
        }

        PerfProbe.CompleteDetailPanelMountProbe();
    }
}

/// <summary>
/// 视频信息区域基类.
/// </summary>
public abstract class VideoInfoSectionBase : LayoutUserControlBase<VideoPlayerInfoSectionDetailViewModel>
{
}