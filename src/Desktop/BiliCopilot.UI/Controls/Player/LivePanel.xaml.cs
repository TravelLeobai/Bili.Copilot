// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 直播间信息与弹幕面板.
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

    private void OnSendClick(object sender, RoutedEventArgs e)
        => ViewModel?.SendLiveDanmakuCommand.Execute(default);

    private void OnFollowClick(object sender, RoutedEventArgs e)
        => ViewModel?.ToggleLiveFollowCommand.Execute(default);
}
