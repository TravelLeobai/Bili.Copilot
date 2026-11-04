// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages.Overlay;

/// <summary>
/// FavoritesPage 页面，布局由 Reactor 渲染.
/// </summary>
public sealed partial class FavoritesPage : FavoritesPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoritesPage"/> class.
    /// </summary>
    public FavoritesPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new FavoritesShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
        => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// FavoritesPage 基类.
/// </summary>
public abstract class FavoritesPageBase : LayoutPageBase<FavoritesPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FavoritesPageBase"/> class.
    /// </summary>
    protected FavoritesPageBase() => ViewModel = this.Get<FavoritesPageViewModel>();
}