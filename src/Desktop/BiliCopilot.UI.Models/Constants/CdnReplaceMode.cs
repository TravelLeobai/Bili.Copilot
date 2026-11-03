// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models.Constants;

/// <summary>
/// CDN 链接替换模式.
/// </summary>
public enum CdnReplaceMode
{
    /// <summary>
    /// 不替换.
    /// </summary>
    None,

    /// <summary>
    /// 替换全部视频链接.
    /// </summary>
    All,

    /// <summary>
    /// 仅替换 PCDN 视频链接.
    /// </summary>
    PcdnOnly,

    /// <summary>
    /// 仅替换走代理拿到的视频链接.
    /// </summary>
    ProxyOnly,
}