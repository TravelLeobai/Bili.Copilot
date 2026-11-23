// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.Diagnostics.Tests;

#if DEBUG
using BiliCopilot.UI.Services.Diagnostics;
using Xunit;

/// <summary>
/// 停滞判定 / 限流 / 落盘 / 监视线程生命周期的单元测试.
/// 全程用假时钟，不真等 180 秒；没有一次 Sleep 是为了掩盖竞态，只有一处 50ms 的启动等待.
/// </summary>
public sealed class PlaybackStallDiagnosticsTests
{
    // 1 tick == 1ms：阈值换算在测试里一眼能算出来.
    private const long Frequency = 1000;

    private const long StartAt = 10_000;

    [Fact]
    public void ReadStallBelowThresholdDoesNotFire()
    {
        var harness = CreateSettledHarness();
        harness.Probe.BeginRead();
        harness.Clock.Advance(PlaybackStallPolicy.ReadStallThresholdMilliseconds - 1);

        var decision = Observe(harness);

        Assert.Equal(PlaybackStallDecision.None, decision);
        Assert.Equal(0, harness.Policy.Stalls);
        Assert.Equal(0, harness.Budget.Attempts);
    }

    [Fact]
    public void ReadStallAtThresholdFires()
    {
        var harness = CreateSettledHarness();
        harness.Probe.BeginRead();
        harness.Clock.Advance(PlaybackStallPolicy.ReadStallThresholdMilliseconds);

        var decision = Observe(harness);

        Assert.Equal(PlaybackStallDecision.Dump, decision);
        Assert.Equal(PlaybackStallKind.ProgressRead, harness.Policy.LastKind);
        Assert.Equal(1, harness.Budget.Attempts);
    }

    [Fact]
    public void ProgressGapBelowThresholdDoesNotFire()
    {
        var harness = CreateSettledHarness();
        harness.Clock.Advance(PlaybackStallPolicy.ProgressGapThresholdMilliseconds - 1);

        Assert.Equal(PlaybackStallDecision.None, Observe(harness));
        Assert.Equal(0, harness.Policy.Stalls);
    }

    [Fact]
    public void ProgressGapAtThresholdFires()
    {
        var harness = CreateSettledHarness();
        harness.Clock.Advance(PlaybackStallPolicy.ProgressGapThresholdMilliseconds);

        var decision = Observe(harness);

        Assert.Equal(PlaybackStallDecision.Dump, decision);
        Assert.Equal(PlaybackStallKind.ProgressGap, harness.Policy.LastKind);
    }

    [Fact]
    public void GcDuringStallSkipsTheDump()
    {
        var harness = CreateSettledHarness();
        harness.Probe.BeginRead();
        harness.Clock.Advance(PlaybackStallPolicy.ReadStallThresholdMilliseconds);

        // 这段里发生了一次 Gen0：耗时不作数.
        harness.Collections.Counts = new PlaybackStallGcCounts(1, 0, 0);
        var decision = Observe(harness);

        Assert.Equal(PlaybackStallDecision.SkippedGc, decision);
        Assert.Equal(1, harness.Policy.Stalls);
        Assert.Equal(1, harness.Policy.SkippedGc);
        Assert.Equal(0, harness.Budget.Attempts);
    }

    [Fact]
    public void PausedReadDoesNotDump()
    {
        var harness = CreateSettledHarness();
        harness.Probe.Observe(playing: false, position: 5d, positionAdvanced: false);
        harness.Probe.BeginRead();
        harness.Clock.Advance(PlaybackStallPolicy.ReadStallThresholdMilliseconds);

        var decision = Observe(harness);

        Assert.Equal(PlaybackStallDecision.SkippedSettling, decision);
        Assert.Equal(0, harness.Budget.Attempts);
    }

    [Fact]
    public void SettlingPeriodSuppressesTheDump()
    {
        // 稳定期从「现在」开始：立刻出现 60ms 读取也不采.
        var clock = new FakeClock(StartAt);
        var collections = new FakeCollections();
        var probe = new PlaybackStallProbe(clock.GetTimestamp, collections.Read);
        var budget = new PlaybackStallDumpBudget(Frequency, clock.GetTimestamp);
        var policy = new PlaybackStallPolicy(budget, Frequency, StartAt);
        probe.Observe(playing: true, position: 1d, positionAdvanced: true);

        probe.BeginRead();
        clock.Advance(PlaybackStallPolicy.ReadStallThresholdMilliseconds);
        var decision = policy.Observe(clock.Now, clock.Now - 10, probe.Current, collections.Counts);

        Assert.Equal(PlaybackStallDecision.SkippedSettling, decision);
        Assert.Equal(0, budget.Attempts);
    }

    [Fact]
    public void SameStallIsNotReportedTwice()
    {
        var harness = CreateSettledHarness();
        harness.Probe.BeginRead();
        harness.Clock.Advance(PlaybackStallPolicy.ReadStallThresholdMilliseconds);
        Assert.Equal(PlaybackStallDecision.Dump, Observe(harness));

        // 同一次读取还在飞：再判多少次都不该再来一次.
        harness.Clock.Advance(100);
        Assert.Equal(PlaybackStallDecision.None, Observe(harness));
        harness.Clock.Advance(100);
        Assert.Equal(PlaybackStallDecision.None, Observe(harness));

        Assert.Equal(1, harness.Policy.Stalls);
        Assert.Equal(1, harness.Budget.Attempts);
    }

    [Fact]
    public void BudgetEnforcesMinimumInterval()
    {
        var clock = new FakeClock();
        var budget = new PlaybackStallDumpBudget(Frequency, clock.GetTimestamp);

        Assert.True(budget.TryReserve(clock.Now));
        clock.Advance((PlaybackStallDumpBudget.DefaultMinimumIntervalSeconds * 1000L) - 1);
        Assert.False(budget.TryReserve(clock.Now));
        clock.Advance(1);
        Assert.True(budget.TryReserve(clock.Now));
        Assert.Equal(2, budget.Attempts);
    }

    [Fact]
    public void BudgetStopsAfterMaximumAttempts()
    {
        var clock = new FakeClock();
        var budget = new PlaybackStallDumpBudget(Frequency, clock.GetTimestamp);

        for (var i = 0; i < PlaybackStallDumpBudget.DefaultMaximumAttempts; i++)
        {
            Assert.True(budget.TryReserve(clock.Now));
            clock.Advance(PlaybackStallDumpBudget.DefaultMinimumIntervalSeconds * 1000L);
        }

        Assert.False(budget.TryReserve(clock.Now));
        Assert.Equal(PlaybackStallDumpBudget.DefaultMaximumAttempts, budget.Attempts);
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void RateLimitIsReportedAsSkipped()
    {
        var harness = CreateSettledHarness();

        // 第一次占满名额.
        harness.Probe.BeginRead();
        harness.Clock.Advance(PlaybackStallPolicy.ReadStallThresholdMilliseconds);
        Assert.Equal(PlaybackStallDecision.Dump, Observe(harness));
        harness.Probe.EndRead();

        // 第二次读取（新编号）在最短间隔之内：跳过原因必须是限流.
        harness.Probe.BeginRead();
        harness.Clock.Advance(PlaybackStallPolicy.ReadStallThresholdMilliseconds);
        var decision = Observe(harness);

        Assert.Equal(PlaybackStallDecision.SkippedRate, decision);
        Assert.Equal(1, harness.Budget.Attempts);
        Assert.Equal(1, harness.Policy.SkippedRate);
    }

    [Fact]
    public void FailedDumpWriteReportsErrorAndLeavesNothing()
    {
        var root = CreateScratchDirectory();
        try
        {
            // 把「父目录是一个文件」当故障注入点：CreateDirectory 必失败.
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            var target = Path.Combine(blocker, "stalls");

            var ok = PlaybackStallDump.TryWrite(target, DateTime.UtcNow, "ctx", out var result, out var error);

            Assert.False(ok);
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Equal(default, result);
            Assert.Empty(Directory.GetFiles(root, "stall-*.dmp", SearchOption.AllDirectories));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void DumpWritesFileWithSidecarAndCleanupRemovesIt()
    {
        var root = CreateScratchDirectory();
        try
        {
            var ok = PlaybackStallDump.TryWrite(root, DateTime.UtcNow, "session=s-test\n", out var result, out var error);

            Assert.True(ok, error);
            Assert.True(result.Bytes > 0);
            Assert.True(result.WriteMilliseconds >= 0);
            var path = Path.Combine(root, result.FileName);
            Assert.True(File.Exists(path), "dump 文件应当存在");
            Assert.True(new FileInfo(path).Length > 4096, "dump 至少该有几十 KB 级别的线程/模块信息");
            Assert.True(File.Exists(Path.ChangeExtension(path, ".txt")), "侧车上下文应当存在");

            Assert.Equal(1, PlaybackStallDump.Cleanup(root));
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void PruneKeepsNewestDumpsAndRemovesSidecars()
    {
        var root = CreateScratchDirectory();
        try
        {
            var stale = new List<string>();
            for (var i = 0; i < PlaybackStallDump.MaximumDumps + 2; i++)
            {
                var path = Path.Combine(root, $"stall-2020010{i}T000000.0000000Z.dmp");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
                File.WriteAllText(Path.ChangeExtension(path, ".txt"), "meta");
                stale.Add(path);
            }

            PlaybackStallDump.Prune(root);

            Assert.Equal(PlaybackStallDump.MaximumDumps, Directory.GetFiles(root, "stall-*.dmp").Length);
            Assert.False(File.Exists(stale[0]), "最旧的应当被删掉");
            Assert.False(File.Exists(Path.ChangeExtension(stale[0], ".txt")), "侧车应当跟着一起删");
            Assert.True(File.Exists(stale[^1]), "最新的应当保留");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WatchStopsWithoutJoiningAndLogsSummary()
    {
        var root = CreateScratchDirectory();
        try
        {
            var log = new FakeStallLog();
            var watch = new PlaybackStallWatch(root, "s-watch", log);
            watch.Start();

            // 只等它把线程跑起来，不用它做判定（判定由上面的策略测试覆盖）.
            await Task.Delay(50);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            watch.Stop();
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 90, "Stop 必须立刻返回，不能等线程退出");
            await watch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(log.InformationLines, line => line.Contains("watch-stopped", StringComparison.Ordinal));
            Assert.Equal(0, watch.Dumps);
            Assert.Empty(Directory.GetFiles(root, "stall-*.dmp"));

            watch.Dispose();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void ProbeKeepsSnapshotCoherentAcrossReadBoundaries()
    {
        var clock = new FakeClock(StartAt);
        var collections = new FakeCollections();
        var probe = new PlaybackStallProbe(clock.GetTimestamp, collections.Read);

        probe.Observe(playing: true, position: 3d, positionAdvanced: true);
        Assert.Equal(0, probe.Current.ReadId);
        clock.Advance(5);
        probe.BeginRead();
        Assert.NotEqual(0, probe.Current.ReadId);
        var readId = probe.Current.ReadId;

        clock.Advance(70);
        Assert.Equal(readId, probe.Current.ReadId);
        Assert.Equal(StartAt + 5, probe.Current.ReadStartedAt);

        probe.EndRead();
        Assert.Equal(0, probe.Current.ReadId);
        Assert.Equal(3d, probe.Current.Position);
        Assert.True(probe.Current.Playing);
    }

    [Fact]
    public void ProbeOnlyBumpsAdvanceWhenPositionMoves()
    {
        var clock = new FakeClock(StartAt);
        var collections = new FakeCollections();
        var probe = new PlaybackStallProbe(clock.GetTimestamp, collections.Read);

        probe.Observe(playing: true, position: 1d, positionAdvanced: true);
        var first = probe.Current.AdvanceId;
        probe.Observe(playing: true, position: 1d, positionAdvanced: false);
        Assert.Equal(first, probe.Current.AdvanceId);
    }

    [Fact]
    public void ProbeCountsEveryPublishSoHeartbeatCanProveLiveness()
    {
        var clock = new FakeClock(StartAt);
        var collections = new FakeCollections();
        var probe = new PlaybackStallProbe(clock.GetTimestamp, collections.Read);

        // 构造里已经发布过一次.
        var initial = probe.Current.PublishCount;
        probe.Observe(playing: true, position: 1d, positionAdvanced: true);
        probe.Observe(playing: true, position: 2d, positionAdvanced: true);
        Assert.Equal(initial + 2, probe.Current.PublishCount);

        // BeginRead / EndRead 不改变「发布过多少次状态」：它们标记的是同一次读取的起止.
        probe.BeginRead();
        probe.EndRead();
        Assert.Equal(initial + 2, probe.Current.PublishCount);
    }

    private static PlaybackStallDecision Observe(Harness harness)
        => harness.Policy.Observe(harness.Clock.Now, harness.Clock.Now - 10, harness.Probe.Current, harness.Collections.Counts);

    private static Harness CreateSettledHarness()
    {
        var clock = new FakeClock(StartAt);
        var collections = new FakeCollections();
        var probe = new PlaybackStallProbe(clock.GetTimestamp, collections.Read);
        var budget = new PlaybackStallDumpBudget(Frequency, clock.GetTimestamp);

        // 稳定期起点放到 6 秒之前：保证当前已经过了 5 秒稳定期.
        var policy = new PlaybackStallPolicy(budget, Frequency, StartAt - 6_000);
        probe.Observe(playing: true, position: 5d, positionAdvanced: true);
        return new Harness(clock, collections, probe, budget, policy);
    }

    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bili-stall-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Harness(
        FakeClock Clock,
        FakeCollections Collections,
        PlaybackStallProbe Probe,
        PlaybackStallDumpBudget Budget,
        PlaybackStallPolicy Policy);
}
#endif
