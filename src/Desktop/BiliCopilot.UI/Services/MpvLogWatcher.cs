// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 包一层 ILogger 交给 MpvPlayer：日志照常往原 logger 写，同时把"画面输出挂了"这类
/// 关键消息挑出来回调出去。
/// 为什么不在 mpv 的事件里判断：mpv 设备丢失（DXGI_ERROR_DEVICE_RESET）在它的 API 里
/// 只体现为一条 error 级日志，事件通道收不到，所以只能从日志这一层捞.
/// </summary>
internal sealed class MpvLogWatcher(ILogger inner, Action<string> onSuspect) : ILogger
{
    private static readonly string[] SuspectKeywords =
    [
        "Device lost",
        "DEVICE_RESET",
        "Couldn't resize swapchain",
        "vulkan_swapchain",
        "Failed to create swapchain",
    ];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
        => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        inner.Log(logLevel, eventId, state, exception, formatter);

        // mpv 内部这些 error 是以 Information 级别转出来的（日志里就是 [INF] [MPV] Log message: error - Device lost!），
        // 按 Warning 以上过滤会把它们全漏掉——之前就是因为这个，设备丢失一直没触发恢复.
        if (logLevel < LogLevel.Information)
        {
            return;
        }

        try
        {
            var text = formatter(state, exception);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (var keyword in SuspectKeywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    onSuspect(text);
                    return;
                }
            }
        }
        catch
        {
            // 观察日志绝不能反过来影响播放.
        }
    }
}

