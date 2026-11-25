// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.MpvCacheOptions.Tests;

using BiliCopilot.UI.Services.Mpv;
using Xunit;

/// <summary>
/// 下发器与状态读取的单元测试.
/// 下发器最关键的一条性质：它不保留「已经下发过」的状态，所以复用播放器时重新调用就能重新生效.
/// </summary>
public sealed class MpvCacheOptionApplierTests
{
    [Fact]
    public void ApplySendsEveryPlannedOption()
    {
        var bridge = new RecordingMpvPropertyBridge();
        var applier = new MpvCacheOptionApplier(bridge);
        var settings = MpvCacheSettings.CreateDefault() with
        {
            ForwardCache = MpvCacheOptionValue.FromMiB(false, 300),
            BackCache = MpvCacheOptionValue.FromMiB(false, 0),
        };

        var result = applier.Apply(settings);

        Assert.Contains((MpvCachePropertyNames.DemuxerMaxBytes, "300MiB"), bridge.Sets);
        Assert.Contains((MpvCachePropertyNames.DemuxerMaxBackBytes, "0MiB"), bridge.Sets);
        Assert.Contains((MpvCachePropertyNames.DemuxerDonateBuffer, "yes"), bridge.Sets);
        Assert.Contains((MpvCachePropertyNames.CacheOnDisk, "no"), bridge.Sets);
        Assert.True(result.AllSucceeded);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void ApplyTwiceSendsTwiceSoReusePathTakesEffect()
    {
        // 同窗口换集复用播放器时，播放器不会重建 client，只靠再调一次 ApplyCacheOptionsAsync 生效；
        // 如果下发器记住「已下发」跳过去，用户改完设置换集就看不到变化.
        var bridge = new RecordingMpvPropertyBridge();
        var applier = new MpvCacheOptionApplier(bridge);
        var settings = MpvCacheSettings.CreateDefault() with
        {
            ForwardCache = MpvCacheOptionValue.FromMiB(false, 300),
        };

        applier.Apply(settings);
        settings = settings with { ForwardCache = MpvCacheOptionValue.FromMiB(false, 420) };
        applier.Apply(settings);

        Assert.Equal(["300MiB", "420MiB"], bridge.ValuesOf(MpvCachePropertyNames.DemuxerMaxBytes));
    }

    [Fact]
    public void FailedSendIsReportedWithErrorInsteadOfThrowing()
    {
        var bridge = new RecordingMpvPropertyBridge();
        bridge.FailOn(MpvCachePropertyNames.DemuxerReadAheadSeconds);
        var applier = new MpvCacheOptionApplier(bridge);

        var result = applier.Apply(MpvCacheSettings.CreateDefault());

        var failure = Assert.Single(result.Failures);
        Assert.Equal(MpvCachePropertyNames.DemuxerReadAheadSeconds, failure.Property);
        Assert.Equal("PropertyFormat", failure.Error);
        Assert.False(result.AllSucceeded);
    }

    [Fact]
    public void AutoOptionsAreReportedButNotSent()
    {
        var bridge = new RecordingMpvPropertyBridge();
        var applier = new MpvCacheOptionApplier(bridge);
        var settings = MpvCacheSettings.CreateDefault() with
        {
            ForwardCache = MpvCacheOptionValue.Auto,
        };

        var result = applier.Apply(settings);

        Assert.Empty(bridge.ValuesOf(MpvCachePropertyNames.DemuxerMaxBytes));
        Assert.Contains(MpvCachePropertyNames.DemuxerMaxBytes, result.SkippedAuto);
        Assert.DoesNotContain(result.Attempts, attempt => attempt.Property == MpvCachePropertyNames.DemuxerMaxBytes);
    }
}

/// <summary>
/// 缓冲实况读取：读不到必须是 null（展示层写「不支持」），不能拿配置上限冒充实测值.
/// </summary>
public sealed class MpvCacheStateReaderTests
{
    [Fact]
    public void BeforeAnyEventReportsNoEventData()
    {
        var reader = new MpvCacheStateReader();

        var snapshot = reader.Read();

        Assert.False(snapshot.HasEventData);
        Assert.Null(snapshot.CacheDurationSeconds);
        Assert.Null(snapshot.BufferingPercent);
        Assert.False(snapshot.IsBuffering);
    }

    [Fact]
    public void EventValuesAreExposedAsMeasuredValues()
    {
        var reader = new MpvCacheStateReader();
        reader.RecordEvent(12 * 1024 * 1024, 3 * 1024 * 1024, true, false, [new MpvSeekableRangeSnapshot(10, 42)]);

        var snapshot = reader.Read();

        Assert.True(snapshot.HasEventData);
        Assert.Equal(12L * 1024 * 1024, snapshot.ForwardBytes);
        Assert.Equal(3L * 1024 * 1024, snapshot.FileCacheBytes);
        Assert.True(snapshot.BofCached);
        Assert.False(snapshot.EofCached);
        var range = Assert.Single(snapshot.SeekableRanges);
        Assert.Equal(10, range.Start);
        Assert.Equal(42, range.End);
    }

    [Fact]
    public void DurationAndBufferingComeFromBridgeAndStayNullWhenUnreadable()
    {
        var bridge = new RecordingMpvPropertyBridge();
        var reader = new MpvCacheStateReader();
        reader.RecordEvent(1024, 0, false, false, []);

        // 桥里没有这两个属性 → 明确是 null，而不是 0.
        var unreadable = reader.Read(bridge);
        Assert.Null(unreadable.CacheDurationSeconds);
        Assert.Null(unreadable.BufferingPercent);
        Assert.False(unreadable.IsBuffering);

        bridge.SetDoubleReadable(MpvCachePropertyNames.DemuxerCacheDuration, 12.5);
        bridge.SetIntegerReadable(MpvCachePropertyNames.CacheBufferingState, 42);

        var readable = reader.Read(bridge);
        Assert.Equal(12.5, readable.CacheDurationSeconds);
        Assert.Equal(42, readable.BufferingPercent);
        Assert.True(readable.IsBuffering);
    }

    [Fact]
    public void ResetClearsPreviousMediaState()
    {
        var reader = new MpvCacheStateReader();
        reader.RecordEvent(4096, 0, false, false, []);

        reader.Reset();

        Assert.False(reader.Read().HasEventData);
        Assert.Equal(0, reader.Read().ForwardBytes);
    }
}
