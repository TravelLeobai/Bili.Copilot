// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentSectionDetailControl"/> class.
    /// </summary>
    public LiveMomentSectionDetailControl()
    {
        Services.StartupTrace.Mark("LiveCard:ctor:begin");
        InitializeComponent();
        Services.StartupTrace.Mark("LiveCard:ctor:end");
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.StartupTrace.Mark("LiveCard:loaded", $"items={ViewModel?.Items.Count ?? -1}");
        UpdateItemWidth();

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
    /// 悬停轻微上浮：只动 Translation，交给 XAML 里的 Vector3Transition 补间，
    /// 不改布局尺寸，所以不会把瀑布流的排版顶歪.
    /// </summary>
    /// <param name="sender">卡片根 Border.</param>
    /// <param name="isHover">是否进入.</param>
    private static void SetCardLift(object sender, bool isHover)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        element.Translation = isHover ? new System.Numerics.Vector3(0, -4, 16) : System.Numerics.Vector3.Zero;
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
/// 直播分区控件基类.
/// </summary>
public abstract class LiveSectionControlBase : LayoutUserControlBase<LiveMomentSectionDetailViewModel>
{
}



