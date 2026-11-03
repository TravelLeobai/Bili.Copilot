// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 直播间信息、互动消息、榜单与大航海面板.
/// </summary>
public sealed partial class LivePanel : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LivePanel"/> class.
    /// </summary>
    public LivePanel() => InitializeComponent();

    /// <summary>
    /// 播放器视图模型.
    /// </summary>
    public PlayerViewModel? ViewModel { get; set; }

    /// <summary>
    /// 判断当前分区是否匹配.
    /// </summary>
    public static Visibility IsTab(int current, int target)
        => current == target ? Visibility.Visible : Visibility.Collapsed;

    private void OnSendClick(object sender, RoutedEventArgs e)
        => ViewModel?.SendLiveDanmakuCommand.Execute(default);

    private void OnFollowClick(object sender, RoutedEventArgs e)
        => ViewModel?.ToggleLiveFollowCommand.Execute(default);

    private void OnRefreshClick(object sender, RoutedEventArgs e)
        => ViewModel?.RefreshLiveInfoCommand.Execute(default);

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
        {
            ViewModel?.SwitchLiveTabCommand.Execute(index);
        }
    }

    private void OnStreamOptionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LiveStreamOptionViewModel option)
        {
            ViewModel?.SwitchLiveStreamCommand.Execute(option);
        }
    }

    private void OnDanmakuToggleClick(object sender, RoutedEventArgs e)
        => ViewModel?.SwitchLiveDanmakuCommand.Execute(default);

    private void OnGiftBagClick(object sender, RoutedEventArgs e)
    {
        // 礼物面板通过 Flyout 打开，这里只刷新一次清单.
        ViewModel?.RefreshLiveInfoCommand.Execute(default);
    }
}