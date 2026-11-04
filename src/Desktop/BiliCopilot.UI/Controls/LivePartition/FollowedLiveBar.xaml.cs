// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Xaml.Controls;

namespace BiliCopilot.UI.Controls.LivePartition;

/// <summary>
/// 直播页顶部的「已关注且正在直播的 UP 主」横条.
/// 点击头像直接进对应直播间.
/// </summary>
public sealed partial class FollowedLiveBar : UserControl
{
    private LivePartitionPageViewModel? _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="FollowedLiveBar"/> class.
    /// </summary>
    public FollowedLiveBar() => InitializeComponent();

    /// <summary>
    /// 页面视图模型，赋值后自动接管关注直播间集合.
    /// </summary>
    public LivePartitionPageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value))
            {
                return;
            }

            _viewModel = value;
            RoomRepeater.ItemsSource = value?.FollowRooms;
        }
    }
}
