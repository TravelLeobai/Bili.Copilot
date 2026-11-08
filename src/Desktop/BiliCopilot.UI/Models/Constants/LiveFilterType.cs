// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models.Constants;

/// <summary>
/// 直播分区的内容类型过滤.
/// </summary>
public enum LiveFilterType
{
    /// <summary>
    /// 全部.
    /// </summary>
    All,

    /// <summary>
    /// 正在直播.
    /// </summary>
    Live,

    /// <summary>
    /// 直播回放 / 录播.
    /// </summary>
    Replay,

    /// <summary>
    /// 切片 UP 主的投稿.
    /// </summary>
    Clip,
}
