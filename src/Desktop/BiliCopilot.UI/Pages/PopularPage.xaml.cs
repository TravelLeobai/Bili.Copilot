// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 流行视频页，布局由 Reactor 渲染.
/// </summary>
public sealed partial class PopularPage : PopularPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="PopularPage"/> class.
    /// </summary>
    public PopularPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new PopularShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded() => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// 流行视频页面基类.
/// </summary>
public abstract class PopularPageBase : LayoutPageBase<PopularPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PopularPageBase"/> class.
    /// </summary>
    protected PopularPageBase() => ViewModel = this.Get<PopularPageViewModel>();
}
