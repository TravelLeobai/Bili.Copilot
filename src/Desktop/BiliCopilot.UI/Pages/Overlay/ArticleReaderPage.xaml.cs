// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.View;
using Richasy.BiliKernel.Models.Article;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages.Overlay;

/// <summary>
/// 文章阅读页面.
/// </summary>
public sealed partial class ArticleReaderPage : ArticleReaderPageBase, IParameterPage
{
    // 页面实例被浮层缓存复用：NavigateTo 命中同类型页面时只调 SetParameter，
    // 不会走 Unloaded，重复订阅会让一篇文章的 Initialized 触发多次 LoadContentAsync.
    private bool _isHooked;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArticleReaderPage"/> class.
    /// </summary>
    public ArticleReaderPage() => InitializeComponent();

    public void SetParameter(object? parameter)
    {
        if (parameter is ArticleIdentifier article)
        {
            if (!_isHooked)
            {
                _isHooked = true;
                Reader.Initialized += OnInitializedAsync;
                ViewModel.ArticleInitialized += OnInitializedAsync;
            }

            ViewModel.InitializeCommand.Execute(article);
        }
    }

    protected override void OnPageUnloaded()
    {
        Reader.ClearContent();
        Reader.Initialized -= OnInitializedAsync;
        ViewModel.ArticleInitialized -= OnInitializedAsync;
        _isHooked = false;
    }

    private async void OnInitializedAsync(object? sender, EventArgs e)
    {
        if (ViewModel.Content is not null && Reader.IsInitialized)
        {
            await Reader.LoadContentAsync(ViewModel.Content);
        }
    }
}

/// <summary>
/// 文章阅读页面基类.
/// </summary>
public abstract class ArticleReaderPageBase : LayoutPageBase<ArticleReaderPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArticleReaderPageBase"/> class.
    /// </summary>
    protected ArticleReaderPageBase() => ViewModel = this.Get<ArticleReaderPageViewModel>();
}
