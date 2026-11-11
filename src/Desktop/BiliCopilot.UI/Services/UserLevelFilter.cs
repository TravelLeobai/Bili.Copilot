// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 低等级用户内容过滤：等级低于 3 的评论与直播弹幕在设置里可以整体屏蔽.
/// 等级为 0 表示接口没给数据，不能当成低等级处理，否则会把正常内容一起吞掉.
/// </summary>
internal static class UserLevelFilter
{
    /// <summary>
    /// 屏蔽阈值：低于这个等级的内容被隐藏.
    /// </summary>
    public const int MinLevel = 3;

    /// <summary>
    /// 设置里是否开启了屏蔽.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.BlockLowLevelUsers, false);

    /// <summary>
    /// 这条内容要不要隐藏.
    /// </summary>
    /// <param name="level">发送者的 B 站等级，0 表示未知.</param>
    public static bool ShouldHide(int level)
        => IsEnabled && level > 0 && level < MinLevel;
}
