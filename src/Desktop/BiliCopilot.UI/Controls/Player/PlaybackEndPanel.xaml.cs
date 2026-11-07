// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using Microsoft.UI.Xaml;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 播放结束页：UP 信息、重播、互动按钮、相关推荐.
/// </summary>
public sealed partial class PlaybackEndPanel : PlayerControlBase
{
    /// <summary>
    /// 宽度低于这个值就把按钮组换到第二行（DIP）.
    /// </summary>
    private const double NarrowThreshold = 1000d;

    private bool _isNarrowLayout;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackEndPanel"/> class.
    /// </summary>
    public PlaybackEndPanel()
    {
        InitializeComponent();
        SizeChanged += OnPanelSizeChanged;
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout(e.NewSize.Width);

    /// <summary>
    /// 宽的时候 UP 信息与按钮并排、按钮靠右；窄的时候按钮落到第二行靠左.
    /// 之前用 AdaptiveTrigger 没生效（窄窗口下按钮溢出被裁掉），改成按实际宽度直接切布局.
    /// </summary>
    /// <param name="width">当前宽度.</param>
    private void ApplyResponsiveLayout(double width)
    {
        var narrow = width < NarrowThreshold;
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
