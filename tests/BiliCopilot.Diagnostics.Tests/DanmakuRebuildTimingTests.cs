// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.Diagnostics.Tests;

using BiliCopilot.UI.Services.Diagnostics;
using Xunit;

/// <summary>
/// 弹幕重建计时的单元测试.
/// 注意：本仓的重建流程没有 CancellationToken，也没有渲染侧的 epoch，所以这里只测「诊断自己的代号」，
/// 不假装渲染结果会被取消.
/// </summary>
public sealed class DanmakuRebuildTimingTests
{
    [Fact]
    public void SegmentSumIsNotTheTotal()
    {
        var timings = new DanmakuRebuildTimings
        {
            BuildMilliseconds = 3.5,
            WriteMilliseconds = 2.0,
            StyleMilliseconds = 1.0,
            SelectMilliseconds = 0.5,
            ResumeMilliseconds = 0.25,
            TotalMilliseconds = 40.0,
        };

        Assert.Equal(7.25, timings.SegmentSumMilliseconds, 3);
        Assert.NotEqual(timings.TotalMilliseconds, timings.SegmentSumMilliseconds);
    }

    [Fact]
    public void FreshGenerationIsCurrent()
    {
        var generation = new DanmakuRebuildGeneration();
        var token = generation.Begin();

        Assert.True(generation.IsCurrent(token));
        Assert.Equal(token, generation.Current);
    }

    [Fact]
    public void NewerRebuildMakesOlderResultStale()
    {
        var generation = new DanmakuRebuildGeneration();
        var first = generation.Begin();
        var second = generation.Begin();

        Assert.False(generation.IsCurrent(first), "旧的那份结果必须被判为过期");
        Assert.True(generation.IsCurrent(second));
    }

    [Fact]
    public void StaleAndFailedAreIndependentFlags()
    {
        var timings = new DanmakuRebuildTimings { Stale = true, Failed = false, Events = 12 };
        Assert.True(timings.Stale);
        Assert.False(timings.Failed);
        Assert.Equal(12, timings.Events);

        timings = new DanmakuRebuildTimings { Stale = false, Failed = true };
        Assert.False(timings.Stale);
        Assert.True(timings.Failed);
    }

    [Fact]
    public void ElapsedConvertsTicks()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Assert.True(DanmakuRebuildTimings.Elapsed(start) >= elapsed - 1);
    }
}
