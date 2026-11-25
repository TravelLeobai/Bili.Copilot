// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.MpvCacheOptions.Tests;

using BiliCopilot.UI.Services.Mpv;
using Xunit;

/// <summary>
/// 缓冲参数下发链路的单元测试.
/// <para>
/// 覆盖三个必须成立的语义：三态（自动不下发 / 显式 0 下发 "0" / 用户值下发）、
/// 复用播放器时重新下发、自定义 mpv.conf 时也下发内置缓冲参数.
/// 另外把「直播不写磁盘缓存」「读不到就留 null」这两个不许走样的地方一并钉住.
/// </para>
/// </summary>
public sealed class MpvCacheOptionPlannerTests
{
    [Fact]
    public void AutoModeSendsNothingForForwardCacheAndReadAhead()
    {
        // 自动 = 跟随 mpv 默认，必须一条都不下发.
        var settings = MpvCacheSettings.CreateDefault() with
        {
            ForwardCache = MpvCacheOptionValue.Auto,
            ReadAheadSeconds = MpvCacheOptionValue.Auto,
        };

        var plan = MpvCacheOptionPlanner.Build(settings);

        Assert.DoesNotContain(plan.Options, option => option.Property == MpvCachePropertyNames.DemuxerMaxBytes);
        Assert.DoesNotContain(plan.Options, option => option.Property == MpvCachePropertyNames.DemuxerReadAheadSeconds);
        Assert.Contains(MpvCachePropertyNames.DemuxerMaxBytes, plan.SkippedAuto);
        Assert.Contains(MpvCachePropertyNames.DemuxerReadAheadSeconds, plan.SkippedAuto);
    }

    [Fact]
    public void ExplicitZeroBackCacheIsSentAsZeroNotSkipped()
    {
        // 这是改动之前的缺陷：0 被当成「没设置」跳过，于是「关掉后向缓存」实际还是 mpv 默认的 50MiB.
        var settings = MpvCacheSettings.CreateDefault() with
        {
            BackCache = MpvCacheOptionValue.FromMiB(false, 0),
        };

        var plan = MpvCacheOptionPlanner.Build(settings);
        var option = Assert.Single(plan.Options, item => item.Property == MpvCachePropertyNames.DemuxerMaxBackBytes);

        Assert.Equal("0MiB", option.Value);
        Assert.DoesNotContain(MpvCachePropertyNames.DemuxerMaxBackBytes, plan.SkippedAuto);
    }

    [Fact]
    public void UserValuesAreSentAsIs()
    {
        var settings = MpvCacheSettings.CreateDefault() with
        {
            ForwardCache = MpvCacheOptionValue.FromMiB(false, 512),
            BackCache = MpvCacheOptionValue.FromMiB(false, 64),
            ReadAheadSeconds = MpvCacheOptionValue.FromNumber(false, 120),
            CacheSeconds = MpvCacheOptionValue.FromNumber(false, 90),
        };

        var plan = MpvCacheOptionPlanner.Build(settings);

        Assert.Equal("512MiB", ValueOf(plan, MpvCachePropertyNames.DemuxerMaxBytes));
        Assert.Equal("64MiB", ValueOf(plan, MpvCachePropertyNames.DemuxerMaxBackBytes));
        Assert.Equal("120", ValueOf(plan, MpvCachePropertyNames.DemuxerReadAheadSeconds));
        Assert.Equal("90", ValueOf(plan, MpvCachePropertyNames.CacheSeconds));
        Assert.Empty(plan.SkippedAuto);
    }

    [Fact]
    public void UnsetCacheSecondsIsNotSent()
    {
        // cache-secs 只有用户明确填了上限才下发；默认（自动）不下发.
        var plan = MpvCacheOptionPlanner.Build(MpvCacheSettings.CreateDefault());

        Assert.DoesNotContain(plan.Options, option => option.Property == MpvCachePropertyNames.CacheSeconds);
        Assert.Contains(MpvCachePropertyNames.CacheSeconds, plan.SkippedAuto);
    }

    [Fact]
    public void CustomMpvConfStillSendsBuiltInCacheOptions()
    {
        // 自定义 mpv.conf 的路径以前整段跳过内置设置，缓冲参数完全失效.
        // 规划层不看配置来源：两种来源生成的计划必须一模一样.
        var builtIn = MpvCacheOptionPlanner.Build(MpvCacheSettings.CreateDefault(configSource: MpvCacheConfigSource.BuiltIn));
        var custom = MpvCacheOptionPlanner.Build(MpvCacheSettings.CreateDefault(configSource: MpvCacheConfigSource.CustomMpvConf));

        Assert.Equal(
            builtIn.Options.Select(option => (option.Property, option.Value)),
            custom.Options.Select(option => (option.Property, option.Value)));
        Assert.Contains(custom.Options, option => option.Property == MpvCachePropertyNames.DemuxerMaxBytes);
        Assert.Contains(custom.Options, option => option.Property == MpvCachePropertyNames.DemuxerMaxBackBytes);
        Assert.Contains(custom.Options, option => option.Property == MpvCachePropertyNames.DemuxerReadAheadSeconds);
        Assert.Contains(custom.Options, option => option.Property == MpvCachePropertyNames.DemuxerDonateBuffer);
        Assert.Contains(custom.Options, option => option.Property == MpvCachePropertyNames.DemuxerSeekableCache);
    }

    [Fact]
    public void LiveDisablesDiskCache()
    {
        // 直播写磁盘缓存会让 mpv 建文件缓存失败，间接把视频轨也带掉，所以一律关.
        var settings = MpvCacheSettings.CreateDefault(isLive: true) with
        {
            CacheOnDisk = true,
            CacheDir = @"D:\cache",
        };

        var plan = MpvCacheOptionPlanner.Build(settings);

        Assert.Equal("no", ValueOf(plan, MpvCachePropertyNames.CacheOnDisk));
        Assert.DoesNotContain(plan.Options, option => option.Property == MpvCachePropertyNames.DemuxerCacheDir);
    }

    [Fact]
    public void CacheDirIsSentOnlyWhenDiskCacheIsOn()
    {
        var settings = MpvCacheSettings.CreateDefault() with
        {
            CacheOnDisk = true,
            CacheDir = @"D:\cache",
        };

        var plan = MpvCacheOptionPlanner.Build(settings);

        Assert.Equal("yes", ValueOf(plan, MpvCachePropertyNames.CacheOnDisk));
        Assert.Equal(@"D:\cache", ValueOf(plan, MpvCachePropertyNames.DemuxerCacheDir));
    }

    private static string ValueOf(MpvCachePlan plan, string property)
        => Assert.Single(plan.Options, option => option.Property == property).Value;
}
