// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 直播间表情（对应 biliuwp-lite 的 LiveRoomEmoticon）.
/// </summary>
public sealed class LiveEmoteItem
{
    /// <summary>
    /// 唯一标识，发送时放在 msg 与 extra.emoticon_unique 里.
    /// </summary>
    public string Unique { get; set; } = string.Empty;

    /// <summary>
    /// 表情文本（如 [doge]），用于弹幕列表里的文字回显.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 表情图片地址.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// 是否未解锁（perm=0）.
    /// </summary>
    public bool IsLocked { get; set; }
}