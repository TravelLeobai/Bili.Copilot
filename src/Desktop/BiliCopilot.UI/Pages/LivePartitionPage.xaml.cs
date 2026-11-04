// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// LivePartitionPage 页面，布局由 Reactor 渲染.
/// </summary>
public sealed partial class LivePartitionPage : LivePartitionPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="LivePartitionPage"/> class.
    /// </summary>
    public LivePartitionPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new LivePartitionShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
        => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// LivePartitionPage 基类.
/// </summary>
public abstract class LivePartitionPageBase : LayoutPageBase<LivePartitionPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LivePartitionPageBase"/> class.
    /// </summary>
    protected LivePartitionPageBase() => ViewModel = this.Get<LivePartitionPageViewModel>();
}