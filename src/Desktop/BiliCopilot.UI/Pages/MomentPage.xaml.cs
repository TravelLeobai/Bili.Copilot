// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 动态页面.
/// </summary>
public sealed partial class MomentPage : MomentPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="MomentPage"/> class.
    /// </summary>
    public MomentPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new MomentShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
        => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// 动态页面基类.
/// </summary>
public abstract class MomentPageBase : LayoutPageBase<MomentPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MomentPageBase"/> class.
    /// </summary>
    protected MomentPageBase() => ViewModel = this.Get<MomentPageViewModel>();
}
