// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// 视频动态卡片控件.
/// </summary>
public sealed partial class VideoMomentPresenter : MomentCardPresenter
{
    /// <summary>
    /// 是否按标准视频比例显示封面.
    /// </summary>
    public static readonly DependencyProperty PreserveCoverAspectRatioProperty = DependencyProperty.Register(
        nameof(PreserveCoverAspectRatio), typeof(bool), typeof(VideoMomentPresenter),
        new PropertyMetadata(false, OnCoverAspectRatioChanged));

    /// <summary>
    /// 是否保持 16:9 封面比例.
    /// </summary>
    public bool PreserveCoverAspectRatio
    {
        get => (bool)GetValue(PreserveCoverAspectRatioProperty);
        set => SetValue(PreserveCoverAspectRatioProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoMomentPresenter"/> class.
    /// </summary>
    public VideoMomentPresenter() => InitializeComponent();

    private static void OnCoverAspectRatioChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((VideoMomentPresenter)sender).UpdateCoverLayout();

    private void UpdateCoverLayout()
    {
        if (CoverRow is null)
        {
            return;
        }

        CoverRow.Height = PreserveCoverAspectRatio ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }
}
