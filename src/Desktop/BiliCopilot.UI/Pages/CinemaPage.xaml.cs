// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// CinemaPage 页面，布局由 Reactor 渲染.
/// </summary>
public sealed partial class CinemaPage : CinemaPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="CinemaPage"/> class.
    /// </summary>
    public CinemaPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new CinemaShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded() => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// CinemaPage 基类.
/// </summary>
public abstract class CinemaPageBase : LayoutPageBase<CinemaPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CinemaPageBase"/> class.
    /// </summary>
    protected CinemaPageBase() => ViewModel = this.Get<CinemaPageViewModel>();
}