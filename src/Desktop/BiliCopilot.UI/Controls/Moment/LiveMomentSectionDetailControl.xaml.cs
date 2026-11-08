// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Richasy.WinUIKernel.Share.Base;
using Windows.System;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 直播分区（动态页新增的分段）.
/// 卡片区是声明式 ItemsRepeater + UniformGridLayout：列数随宽度自动增减，
/// 行高按该行最高的卡片算，标题两行也不会被裁.
/// </summary>
public sealed partial class LiveMomentSectionDetailControl : LiveSectionControlBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentSectionDetailControl"/> class.
    /// </summary>
    public LiveMomentSectionDetailControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 首帧的滚动位置可能被焦点/首项对齐推到非 0，导致第一行卡片顶部被切掉.
            // 排到下一个调度回合把偏移归零（只做一次，用户之后的滚动不动）.
            this.Get<Microsoft.UI.Dispatching.DispatcherQueue>().TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    try
                    {
                        CardScroller?.ChangeView(null, 0, null, true);
                    }
                    catch
                    {
                    }
                });

            var count = ViewModel?.Items.Count ?? 0;
            WriteCrashLog("LiveMomentSection.OnLoaded", null, $"items={count}");
        }
        catch (Exception ex)
        {
            WriteCrashLog("LiveMomentSection.OnLoaded", ex, string.Empty);
        }
    }

    private static void WriteCrashLog(string tag, Exception? ex, string extra)
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
                $"{DateTime.Now:O} [{tag}] {extra}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
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
