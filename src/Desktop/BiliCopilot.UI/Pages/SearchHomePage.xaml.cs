// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 搜索首页，界面上只有一个搜索框.
/// </summary>
public sealed partial class SearchHomePage : Page
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SearchHomePage"/> class.
    /// </summary>
    public SearchHomePage()
    {
        InitializeComponent();
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        var keyword = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return;
        }

        // 交给现有的 overlay 搜索页处理，这里只负责把关键字递过去.
        var nav = this.Get<BiliCopilot.UI.ViewModels.Core.NavigationViewModel>();
        nav.NavigateToOver(typeof(BiliCopilot.UI.Pages.Overlay.SearchPage), keyword);
        SearchBox.Text = string.Empty;
    }
}