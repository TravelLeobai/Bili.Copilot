// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// VideoPartitionPage 页面，布局由 Reactor 渲染.
/// </summary>
public sealed partial class VideoPartitionPage : VideoPartitionPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPartitionPage"/> class.
    /// </summary>
    public VideoPartitionPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new VideoPartitionShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded() => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// VideoPartitionPage 基类.
/// </summary>
public abstract class VideoPartitionPageBase : LayoutPageBase<VideoPartitionPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPartitionPageBase"/> class.
    /// </summary>
    protected VideoPartitionPageBase() => ViewModel = this.Get<VideoPartitionPageViewModel>();
}