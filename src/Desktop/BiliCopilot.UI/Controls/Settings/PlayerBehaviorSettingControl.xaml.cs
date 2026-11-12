// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Controls.Settings;

/// <summary>
/// 播放器行为设置控件.
/// </summary>
public sealed partial class PlayerBehaviorSettingControl : SettingsPageControlBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerBehaviorSettingControl"/> class.
    /// </summary>
    public PlayerBehaviorSettingControl() => InitializeComponent();

    /// <summary>
    /// 「画质偏好」只在关闭画质解锁时出现：开着解锁时以画质解锁里的偏好为准，
    /// 这条老设置留着会让人以为还在生效.
    /// </summary>
    /// <param name="unlockEnabled">画质解锁是否开启.</param>
    /// <returns>可见性.</returns>
    internal static Visibility GetPreferQualityVisibility(bool unlockEnabled)
        => unlockEnabled ? Visibility.Collapsed : Visibility.Visible;
}
