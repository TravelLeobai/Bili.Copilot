// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models.Constants;

/// <summary>
/// 画质解锁里的画质偏好：决定解锁时向解析服务要哪一档，以及进视频后默认选哪档.
/// </summary>
public enum UnlockQualityPreference
{
    /// <summary>
    /// 自动选择最高画质（能要到多高就要多高）.
    /// </summary>
    AutoHighest,

    /// <summary>
    /// 1080P 60 帧优先（qn=116）；没有 60 帧时退 1080P 高码率（qn=112），
    /// 两者都没有才退到当前档位.
    /// </summary>
    FullHd60,
}
