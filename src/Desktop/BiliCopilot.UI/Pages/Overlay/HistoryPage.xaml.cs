// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages.Overlay;

/// <summary>
/// HistoryPage 页面，布局由 Reactor 渲染.
/// </summary>
public sealed partial class HistoryPage : HistoryPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryPage"/> class.
    /// </summary>
    public HistoryPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new HistoryShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
        => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// HistoryPage 基类.
/// </summary>
public abstract class HistoryPageBase : LayoutPageBase<HistoryPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryPageBase"/> class.
    /// </summary>
    protected HistoryPageBase() => ViewModel = this.Get<HistoryPageViewModel>();
}