// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// ArticlePartitionPage 页面，布局由 Reactor 渲染.
/// </summary>
public sealed partial class ArticlePartitionPage : ArticlePartitionPageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArticlePartitionPage"/> class.
    /// </summary>
    public ArticlePartitionPage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new ArticlePartitionShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded() => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// ArticlePartitionPage 基类.
/// </summary>
public abstract class ArticlePartitionPageBase : LayoutPageBase<ArticlePartitionPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArticlePartitionPageBase"/> class.
    /// </summary>
    protected ArticlePartitionPageBase() => ViewModel = this.Get<ArticlePartitionPageViewModel>();
}