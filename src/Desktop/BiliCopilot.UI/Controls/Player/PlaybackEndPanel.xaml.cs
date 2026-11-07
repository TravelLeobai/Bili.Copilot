// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using Microsoft.UI.Xaml;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 播放结束页：UP 信息、重播、互动按钮、相关推荐.
/// </summary>
public sealed partial class PlaybackEndPanel : PlayerControlBase
{
    /// <summary>
    /// 兜底阈值（DIP）：还没量到实际所需宽度时用它.
    /// </summary>
    private const double FallbackNarrowThreshold = 760d;

    private bool _isNarrowLayout;
    private double _requiredWidth;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackEndPanel"/> class.
    /// </summary>
    public PlaybackEndPanel()
    {
        InitializeComponent();
        SizeChanged += OnPanelSizeChanged;
    }

    /// <summary>
    /// 关注 / 取消关注：走连接器的开关命令，结束后按最终状态给提示.
    /// 命令内部吞掉异常，所以用"状态有没有变"判断成败，而不是只看异常.
    /// </summary>
    private async void OnFollowClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        var connector = viewModel?.EndConnector;
        if (viewModel is null || connector is null)
        {
            return;
        }

        var before = connector.IsFollow;
        await connector.ToggleFollowCommand.ExecuteAsync(default);
        var after = connector.IsFollow;

        if (after == before)
        {
            viewModel.Window?.ShowTip("关注操作失败，请稍后再试", InfoType.Error);
            return;
        }

        viewModel.Window?.ShowTip(after ? "已关注" : "已取消关注", InfoType.Success);
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout(e.NewSize.Width);

    /// <summary>
    /// 宽的时候 UP 信息与按钮并排、按钮靠右；窄的时候按钮落到第二行靠左.
    /// 阈值不用写死的数字：先在宽态下量一次"UP 信息 + 按钮组 + 间距"需要多宽，
    /// 之后按这个实测值判断，避免窗口还够用就提前换行.
    /// </summary>
    /// <param name="width">当前宽度.</param>
    private void ApplyResponsiveLayout(double width)
    {
        if (!_isNarrowLayout && _requiredWidth <= 0)
        {
            var required = EndUpInfo.ActualWidth + EndActions.ActualWidth + EndHeader.ColumnSpacing;
            if (required > 0)
            {
                // 留 16 DIP 余量，避免刚好卡在边界上来回抖.
                _requiredWidth = required + 16;
            }
        }

        var threshold = _requiredWidth > 0 ? _requiredWidth : FallbackNarrowThreshold;
        var narrow = width < threshold;
        if (narrow == _isNarrowLayout)
        {
            return;
        }

        _isNarrowLayout = narrow;

        if (narrow)
        {
            Grid.SetRow(EndActions, 1);
            Grid.SetColumn(EndActions, 0);
            Grid.SetColumnSpan(EndActions, 2);
            EndActions.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            Grid.SetRow(EndActions, 0);
            Grid.SetColumn(EndActions, 1);
            Grid.SetColumnSpan(EndActions, 1);
            EndActions.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }
}
