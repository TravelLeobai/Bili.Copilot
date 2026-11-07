// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Richasy.BiliKernel.Models.Search;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// 应用搜索框.
/// </summary>
public sealed partial class AppSearchBox : AppSearchBoxBase
{
    private string _prefilledText = string.Empty;
    private bool _suppressSuggestions;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSearchBox"/> class.
    /// </summary>
    public AppSearchBox() => InitializeComponent();

    /// <summary>
    /// 预填搜索词.
    /// 搜索结果页顶栏复用这个搜索框：进来时带上当前关键词，用户点一下（点击或获得焦点）就会清空，
    /// 等于直接开始新搜索.
    /// </summary>
    /// <param name="keyword">要预填的关键词.</param>
    public void PrefillKeyword(string keyword)
    {
        var text = keyword ?? string.Empty;
        _prefilledText = text;
        _suppressSuggestions = true;
        ViewModel.Keyword = text;
        Box.Text = text;

        // 预填不该把建议列表也弹出来：清掉残留建议 + 抑制期内再强制收一次.
        ViewModel.Suggestion.Clear();
        Box.IsSuggestionListOpen = false;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ViewModel.Suggestion.Clear();
            Box.IsSuggestionListOpen = false;
        });
    }

    private void OnBoxPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // 用户开始自己操作了，放开建议.
        _suppressSuggestions = false;

        // 只有"还是上次预填的那个词"才清空：用户自己输入的过程中点击不该被抹掉.
        if (!string.IsNullOrEmpty(_prefilledText) && string.Equals(Box.Text, _prefilledText, StringComparison.Ordinal))
        {
            _prefilledText = string.Empty;
            Box.Text = string.Empty;
            ViewModel.Keyword = string.Empty;
            Box.IsSuggestionListOpen = false;
        }
    }

    protected override void OnControlUnloaded()
        => HotSearchView.ItemsSource = null;

    private void OnKeywordChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressSuggestions)
        {
            // 预填阶段不拉建议，也不让列表弹出来.
            Box.IsSuggestionListOpen = false;
            return;
        }

        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.ReloadSuggestionsCommand.Execute(default);
        }
    }

    private void OnBoxGettingFocus(UIElement sender, Microsoft.UI.Xaml.Input.GettingFocusEventArgs args)
    {
        if (!Box.IsSuggestionListOpen)
        {
            ViewModel.Keyword = " ";
            ViewModel.Keyword = string.Empty;
        }
    }

    private void OnHotSearchFlyoutOpened(object sender, object e)
        => ViewModel.LoadHotSearchCommand.Execute(default);

    private void OnHotSearchItemInvoked(object sender, ItemClickEventArgs args)
    {
        var context = args.ClickedItem as HotSearchItem;
        if (context is not null)
        {
            ViewModel.Keyword = context.Keyword;
            var vm = new SearchSuggestItemViewModel(new SearchSuggestItem(context.Keyword, context.Keyword));
            ViewModel.SearchCommand.Execute(vm);
        }

        HotSearchFlyout.Hide();
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchSuggestItemViewModel item)
        {
            ViewModel.SearchCommand.Execute(item);
        }
        else if (!string.IsNullOrEmpty(args.QueryText))
        {
            var vm = new SearchSuggestItemViewModel(new SearchSuggestItem(args.QueryText, args.QueryText));
            ViewModel.SearchCommand.Execute(vm);
        }

        ViewModel.TryCancelSuggestCommand.Execute(default);

        // 建议浮层是 Popup，跳到结果页后它还会浮在最上层，提交时就要收掉.
        Box.IsSuggestionListOpen = false;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            Box.IsSuggestionListOpen = false;
            ViewModel.Suggestion.Clear();
        });
    }
}

/// <summary>
/// 应用搜索框的基类.
/// </summary>
public abstract class AppSearchBoxBase : LayoutUserControlBase<SearchBoxViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AppSearchBoxBase"/> class.
    /// </summary>
    protected AppSearchBoxBase() => ViewModel = this.Get<SearchBoxViewModel>();
}



