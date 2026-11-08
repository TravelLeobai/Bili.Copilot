// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Richasy.WinUIKernel.Share.Base;
using Windows.System;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 直播分区（动态页新增的分段）.
/// </summary>
public sealed partial class LiveMomentSectionDetailControl : LiveSectionControlBase
{
    private const double MinCardWidth = 240d;
    private const double CardGap = 16d;
    private const double SidePadding = 32d;
    // 卡片内容约 236 高，加上容器 margin 的 16 才不会在底部被切.
    private const double CardHeight = 272d;
    private double _lastSlotWidth;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentSectionDetailControl"/> class.
    /// </summary>
    public LiveMomentSectionDetailControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateCardSize();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && UperView is not null)
        {
            UperView.SelectionChanged -= OnUperSelectionChanged;
            UperView.SelectionChanged += OnUperSelectionChanged;
            ViewModel.Items.CollectionChanged -= OnItemsChanged;
            ViewModel.Items.CollectionChanged += OnItemsChanged;
        }

        // 首帧时 ItemsPanelRoot 可能还没建好、宽度也还没落定，排到下一个调度回合再算一次.
        this.Get<Microsoft.UI.Dispatching.DispatcherQueue>().TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateCardSize);
        UpdateCardSize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (UperView is not null)
        {
            UperView.SelectionChanged -= OnUperSelectionChanged;
        }

        if (ViewModel is not null)
        {
            ViewModel.Items.CollectionChanged -= OnItemsChanged;
        }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateCardSize();

    private void OnUperSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is LiveSectionUperViewModel uper && ViewModel is not null)
        {
            ViewModel.SelectUper(uper);
        }
    }

    private void OnCardViewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardSize();

    /// <summary>
    /// 按可用宽度算列数，把卡片槽宽写进 ItemsWrapGrid：每张卡等宽，列数随窗口增减.
    /// </summary>
    private void UpdateCardSize()
    {
        if (CardView?.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }

        var available = CardView.ActualWidth - SidePadding;
        if (available <= 0)
        {
            return;
        }

        var count = (int)Math.Max(1, Math.Floor((available + CardGap) / (MinCardWidth + CardGap)));
        var cardWidth = Math.Floor((available - ((count - 1) * CardGap)) / count);
        var slotWidth = cardWidth + CardGap;

        if (Math.Abs(slotWidth - _lastSlotWidth) < 0.5)
        {
            return;
        }

        _lastSlotWidth = slotWidth;
        panel.ItemWidth = slotWidth;
        panel.ItemHeight = CardHeight;
    }

    private void OnCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LiveSectionCardViewModel card)
        {
            card.PlayCommand.Execute(default);
        }
    }

    private void OnCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // 卡片主体点击播放；Globe 按钮有自己的 Click，不会走到这里.
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
/// 直播分区控件基类.
/// </summary>
public abstract class LiveSectionControlBase : LayoutUserControlBase<LiveMomentSectionDetailViewModel>
{
}


