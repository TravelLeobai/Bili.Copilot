// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models.Constants;

/// <summary>
/// 播放器引擎类型.
/// </summary>
public enum PlayerEngineType
{
    /// <summary>
    /// 内置 mpv 播放器.
    /// </summary>
    Mpv,

    /// <summary>
    /// WebView2 网页播放器（参考 BiliLite 的 Shaka/Mpegts 方案）.
    /// </summary>
    Web,
}