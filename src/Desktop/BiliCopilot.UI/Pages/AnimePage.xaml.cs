// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// AnimePage 页面，布局由 Reactor 渲染.
/// </summary>
public sealed partial class AnimePage : AnimePageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimePage"/> class.
    /// </summary>
    public AnimePage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new AnimeShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded() => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// AnimePage 基类.
/// </summary>
public abstract class AnimePageBase : LayoutPageBase<AnimePageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AnimePageBase"/> class.
    /// </summary>
    protected AnimePageBase() => ViewModel = this.Get<AnimePageViewModel>();
}