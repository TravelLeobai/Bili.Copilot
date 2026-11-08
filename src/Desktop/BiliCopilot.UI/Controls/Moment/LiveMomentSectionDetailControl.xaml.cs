// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Richasy.WinUIKernel.Share.Base;
using System.Collections.ObjectModel;
using Windows.System;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 直播分区（动态页新增的分段）.
/// </summary>
public sealed partial class LiveMomentSectionDetailControl : LiveSectionControlBase
{
    private const double MinCardWidth = 240d;
    private const double CardGap = 16d;
    private int _columnCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentSectionDetailControl"/> class.
    /// </summary>
    public LiveMomentSectionDetailControl()
    {
        InitializeComponent();
        Columns = [];
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 瀑布流各列.
    /// </summary>
    public ObservableCollection<LiveCardColumn> Columns { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Items.CollectionChanged -= OnItemsChanged;
            ViewModel.Items.CollectionChanged += OnItemsChanged;
        }

        if (UperView is not null)
        {
            UperView.SelectionChanged -= OnUperSelectionChanged;
            UperView.SelectionChanged += OnUperSelectionChanged;
        }

        RebuildColumns();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Items.CollectionChanged -= OnItemsChanged;
        }

        if (UperView is not null)
        {
            UperView.SelectionChanged -= OnUperSelectionChanged;
        }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildColumns();

    private void OnUperSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is LiveSectionUperViewModel uper && ViewModel is not null)
        {
            ViewModel.SelectUper(uper);
        }
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width - 32; // ScrollViewer 的左右 padding
        var count = (int)Math.Max(1, Math.Floor((width + CardGap) / (MinCardWidth + CardGap)));
        if (count == _columnCount)
        {
            return;
        }

        _columnCount = count;
        RebuildColumns();
    }

    private void RebuildColumns()
    {
        if (ViewModel is null)
        {
            return;
        }

        var count = Math.Max(1, _columnCount);
        if (count > ViewModel.Items.Count)
        {
            count = Math.Max(1, ViewModel.Items.Count);
        }

        // 列数变了就重建列；列数没变则只把内容按列重新分配.
        if (Columns.Count != count)
        {
            Columns.Clear();
            for (var i = 0; i < count; i++)
            {
                Columns.Add(new LiveCardColumn());
            }
        }

        foreach (var column in Columns)
        {
            column.Cards.Clear();
        }

        for (var i = 0; i < ViewModel.Items.Count; i++)
        {
            Columns[i % Columns.Count].Cards.Add(ViewModel.Items[i]);
        }
    }

    private void OnCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is LiveSectionCardViewModel card)
        {
            card.PlayCommand.Execute(default);
        }
    }

    private async void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.DataContext is LiveSectionCardViewModel card
            && !string.IsNullOrEmpty(card.BrowserUrl))
        {
            await Launcher.LaunchUriAsync(new Uri(card.BrowserUrl));
        }
    }
}

/// <summary>
/// 瀑布流的一列.
/// </summary>
public sealed class LiveCardColumn
{
    /// <summary>
    /// 这一列的卡片.
    /// </summary>
    public ObservableCollection<LiveSectionCardViewModel> Cards { get; } = [];
}

/// <summary>
/// 直播分区控件基类.
/// </summary>
public abstract class LiveSectionControlBase : LayoutUserControlBase<LiveMomentSectionDetailViewModel>
{
}
