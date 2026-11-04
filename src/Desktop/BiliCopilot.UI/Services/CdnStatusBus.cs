// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services;

/// <summary>
/// CDN 优选 / 加速的状态广播.
/// 设置页操和播放器都往这里报，播放器收到的消息直接打成一条弹幕.
/// </summary>
internal static class CdnStatusBus
{
    /// <summary>
    /// 有新的执行状态.
    /// </summary>
    public static event EventHandler<string>? StatusReported;

    /// <summary>
    /// 最近一条状态.
    /// </summary>
    public static string LastStatus { get; private set; } = string.Empty;

    /// <summary>
    /// 报一条状态.
    /// </summary>
    /// <param name="message">要显示的内容.</param>
    public static void Report(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LastStatus = message;
        Serilog.Log.Information("[CDN] {Message}", message);
        StatusReported?.Invoke(null, message);
    }
}
