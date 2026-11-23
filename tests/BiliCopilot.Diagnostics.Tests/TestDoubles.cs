// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.Diagnostics.Tests;

#if DEBUG
using BiliCopilot.UI.Services.Diagnostics;

/// <summary>
/// 可注入的时钟：一个 tick 就是 1 毫秒，测试里不用真等 180 秒.
/// </summary>
internal sealed class FakeClock
{
    private long _now;

    public FakeClock(long start = 0) => _now = start;

    public long Now => _now;

    public long GetTimestamp() => _now;

    public void Advance(long milliseconds) => _now += milliseconds;

    public void Set(long value) => _now = value;
}

/// <summary>
/// 收集日志的假 logger（诊断只通过这个接口写日志）.
/// </summary>
internal sealed class FakeStallLog : IPlaybackStallLog
{
    public List<string> InformationLines { get; } = [];

    public List<string> WarningLines { get; } = [];

    public bool IsInformationEnabled { get; set; } = true;

    public void Information(string messageTemplate, params object?[] values)
        => InformationLines.Add(Render(messageTemplate, values));

    public void Warning(string messageTemplate, params object?[] values)
        => WarningLines.Add(Render(messageTemplate, values));

    private static string Render(string template, object?[] values)
    {
        var builder = new System.Text.StringBuilder(template);
        for (var i = 0; i < values.Length; i++)
        {
            builder.Append(" | ").Append(values[i]);
        }

        return builder.ToString();
    }
}

/// <summary>
/// 固定 GC 计数（测试里我们想控制「这段里有没有发生 GC」）.
/// </summary>
internal sealed class FakeCollections
{
    public PlaybackStallGcCounts Counts { get; set; } = new(0, 0, 0);

    public PlaybackStallGcCounts Read() => Counts;
}
#endif
