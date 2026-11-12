// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.Toolkits;
using Richasy.WinUIKernel.Share.Base;
using Windows.System;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 直播分区（动态页新增的分段）.
/// 卡片区是声明式 ItemsRepeater + UniformGridLayout：
/// ItemsStretch=None 让卡片保持内容高度（行高由该行最高卡片决定，互不拉伸），
/// MinItemWidth 在代码里按可用宽度算，所以列宽均分、右侧不留空白.
/// </summary>
public sealed partial class LiveMomentSectionDetailControl : LiveSectionControlBase
{
    private const double MinCardWidth = 240d;
    private const double CardGap = 16d;
    private const double SidePadding = 32d;

    private double _lastItemWidth;
    private MomentSentinelPager? _pager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentSectionDetailControl"/> class.
    /// </summary>
    public LiveMomentSectionDetailControl()
    {
        Services.StartupTrace.Mark("LiveCard:ctor:begin");
        InitializeComponent();
        Services.StartupTrace.Mark("LiveCard:ctor:end");
        _pager = new MomentSentinelPager("直播", OnPagerLoadNextBatch);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pager?.Detach();
        if (ViewModel is not null)
        {
            ViewModel.Items.CollectionChanged -= OnItemsCollectionChanged;
        }
    }

    private void OnItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => _pager?.QueueFillCheck();

    private bool OnPagerLoadNextBatch()
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return false;
        }

        var loaded = viewModel.LoadNextBatch();
        if (loaded)
        {
            LogRealizedCardCount();
        }

        return loaded;
    }

    /// <summary>
    /// 记一下「列表里有多少张、真正建了多少张卡片」，用来判断网格有没有在虚拟化.
    /// </summary>
    private void LogRealizedCardCount()
    {
        var repeater = FindRepeater(this);
        if (repeater is null || ViewModel is null)
        {
            return;
        }

        Serilog.Log.Information(
            "动态哨兵（直播）：列表 {Total} 条，已建卡片 {Realized} 张",
            ViewModel.Items.Count,
            Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(repeater));
    }

    private static ItemsRepeater? FindRepeater(DependencyObject parent)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ItemsRepeater repeater)
            {
                return repeater;
            }

            if (FindRepeater(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.StartupTrace.Mark("LiveCard:loaded", $"items={ViewModel?.Items.Count ?? -1}");
        UpdateItemWidth();

        // 底部哨兵挂到外层 ScrollViewer 上；同时盯住集合变化，
        // 首屏卡片到齐后补到撑满一屏就停.
        _pager?.Reset();
        _pager?.Attach(CardSentinel);
        if (ViewModel is not null)
        {
            ViewModel.Items.CollectionChanged -= OnItemsCollectionChanged;
            ViewModel.Items.CollectionChanged += OnItemsCollectionChanged;
        }

        LogRealizedCardCount();

        // 首帧尺寸可能还是 0，下一轮调度再算一次；顺手把滚动偏移归零，
        // 否则第一行卡片上半截（作者头像行）会被初始偏移切掉.
        this.Get<Microsoft.UI.Dispatching.DispatcherQueue>().TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                UpdateItemWidth();
                try
                {
                    CardScroller?.ChangeView(null, 0, null, true);
                }
                catch
                {
                    // 归零失败不影响使用.
                }

                // 首帧时 ItemsRepeater 还没把卡片放出来，归零会落在空内容上；
                // 再等两个调度周期补一次，否则第一行的作者行还是会被切掉.
                var again = this.Get<Microsoft.UI.Dispatching.DispatcherQueue>();
                _ = System.Threading.Tasks.Task.Delay(150).ContinueWith(
                    _ => again.TryEnqueue(() =>
                    {
                        try
                        {
                            CardScroller?.ChangeView(null, 0, null, true);
                        }
                        catch
                        {
                        }
                    }));
            });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateItemWidth();

    /// <summary>
    /// 按可用宽度算列数，把"恰好能均分的卡片宽"写回 UniformGridLayout.
    /// </summary>
    private void UpdateItemWidth()
    {
        try
        {
            if (CardLayout is null)
            {
                return;
            }

            var width = ActualWidth > 0 ? ActualWidth : (CardScroller?.ActualWidth ?? 0);
            if (width <= 0)
            {
                return;
            }

            var available = Math.Max(MinCardWidth, width - SidePadding);
            var count = (int)Math.Max(1, Math.Floor((available + CardGap) / (MinCardWidth + CardGap)));
            var cardWidth = Math.Floor((available - ((count - 1) * CardGap)) / count);
            if (Math.Abs(cardWidth - _lastItemWidth) < 0.5)
            {
                return;
            }

            Services.StartupTrace.Mark("LiveCard:item-width", $"{_lastItemWidth} -> {cardWidth}");
            _lastItemWidth = cardWidth;
            CardLayout.MinItemWidth = cardWidth;
        }
        catch (Exception ex)
        {
            WriteCrashLog("LiveMomentSection.UpdateItemWidth", ex);
        }
    }

    private static void WriteCrashLog(string tag, Exception ex)
    {
        try
        {
            var folder = App.LoggerFolder;
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, $"crash-{DateTime.Now:yyyy-MM-dd}.txt"),
                $"{DateTime.Now:O} [{tag}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志本身失败不影响界面.
        }
    }

    private void OnUperSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is LiveSectionUperViewModel uper && ViewModel is not null)
        {
            ViewModel.SelectUper(uper);
        }
    }

    private void OnCardPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => SetCardLift(sender, true);

    private void OnCardPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => SetCardLift(sender, false);

    /// <summary>
    /// 悬停轻微上浮.
    /// 不能用 Translation / TranslationTransition：卡片的 CardControl 用了
    /// ElementCompositionPreview.GetElementVisual，同一元素再碰这两个 API 会抛
    /// UnauthorizedAccessException（实测直接崩在 PointerEntered 里）.
    /// 改用传统 RenderTransform 平移，不动布局尺寸，也不碰合成层.
    /// </summary>
    /// <param name="sender">卡片根元素.</param>
    /// <param name="isHover">是否进入.</param>
    private static void SetCardLift(object sender, bool isHover)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        if (element.RenderTransform is not Microsoft.UI.Xaml.Media.TranslateTransform transform)
        {
            transform = new Microsoft.UI.Xaml.Media.TranslateTransform();
            element.RenderTransform = transform;
        }

        transform.Y = isHover ? -4 : 0;

        if (element.RenderTransformOrigin != new Windows.Foundation.Point(0.5, 0.5))
        {
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }
    }
    private void OnCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // x:Bind 模板里元素不一定带 DataContext，用 Tag="{x:Bind}" 直接拿到条目 VM.
        var card = ResolveCard(sender);
        Services.StartupTrace.Mark("LiveCard:tapped", card?.Title ?? "no-vm");
        card?.PlayCommand.Execute(default);
    }

    private void OnCardPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var card = ResolveCard(sender);
        Services.StartupTrace.Mark("LiveCard:pointer-released", card?.Title ?? "no-vm");
        card?.PlayCommand.Execute(default);
    }

    private static LiveSectionCardViewModel? ResolveCard(object sender)
    {
        if (sender is FrameworkElement element)
        {
            if (element.Tag is LiveSectionCardViewModel tagged)
            {
                return tagged;
            }

            if (element.DataContext is LiveSectionCardViewModel fromContext)
            {
                return fromContext;
            }

            // 事件从内层元素冒泡上来时，往上找带 VM 的祖先.
            var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
            while (parent is not null)
            {
                if (parent is FrameworkElement parentElement)
                {
                    if (parentElement.Tag is LiveSectionCardViewModel parentTagged)
                    {
                        return parentTagged;
                    }

                    if (parentElement.DataContext is LiveSectionCardViewModel parentContext)
                    {
                        return parentContext;
                    }
                }

                parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
            }
        }

        return null;
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








