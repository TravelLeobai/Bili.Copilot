// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages.Overlay;

/// <summary>
/// ViewLaterPage 页面，布局由 Reactor 渲染.
/// </summary>
public sealed partial class ViewLaterPage : ViewLaterPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="ViewLaterPage"/> class.
    /// </summary>
    public ViewLaterPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new ViewLaterShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
        => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// ViewLaterPage 基类.
/// </summary>
public abstract class ViewLaterPageBase : LayoutPageBase<ViewLaterPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ViewLaterPageBase"/> class.
    /// </summary>
    protected ViewLaterPageBase() => ViewModel = this.Get<ViewLaterPageViewModel>();
}