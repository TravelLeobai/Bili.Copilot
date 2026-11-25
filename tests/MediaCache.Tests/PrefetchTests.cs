// Copyright (c) Bili Copilot. All rights reserved.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using BiliCopilot.UI.Services.Media;

namespace MediaCache.Tests;

/// <summary>
/// 主动填充（预取）的测试：全部用本地假 CDN + 真代理 + 真缓存跑，不打外网。
/// </summary>
internal static class PrefetchTests
{
    private const int BlockSize = 256 * 1024;
    private const long TwoMegabitsPerSecond = 2_000_000;

    public static void Register()
    {
        TestHost.Add("预取：主动填充后，播放请求直接命中缓存、不再打上游", async () =>
        {
            using var fixture = PrefetchFixture.Start(8 * 1024 * 1024);
            var target = fixture.RegisterPrefetchTarget("BV1prefetchHit");

            var outcome = await fixture.Prefetch.PrefetchAsync(
                target,
                MediaPrefetchStage.Immediate,
                maxSeconds: 0,
                CancellationToken.None);

            Assert.True(outcome.Success, "阶段一应成功，实际：" + outcome.Note);
            Assert.True(outcome.ReceivedBytes >= 256 * 1024, "应至少拉到一块，实际 " + outcome.ReceivedBytes);

            // 播放侧另起一条登记（不同归属键），身份相同 → 同一个缓存条目.
            var playback = fixture.RegisterPlayback("BV1prefetchHit");
            using var client = CreateClient();

            // 先 HEAD 一次让播放登记完成自己的长度探测，再记录上游请求数.
            using (var head = new HttpRequestMessage(HttpMethod.Head, playback.ProxyUrl))
            {
                using var headResponse = await client.SendAsync(head);
                Assert.True((int)headResponse.StatusCode is 200 or 206, "HEAD 状态码 " + (int)headResponse.StatusCode);
            }

            var upstreamBefore = fixture.Cdn.RequestCount;
            var hitBefore = fixture.Proxy.Cache.GetStatistics().HitBytes;

            var body = await GetRangeAsync(client, playback.ProxyUrl, 0, BlockSize - 1);
            Assert.Equal(BlockSize, body.Length, "响应长度");
            Assert.True(
                body.AsSpan().SequenceEqual(fixture.Cdn.Payload.AsSpan(0, BlockSize)),
                "预取内容应与源一致");

            Assert.Equal(upstreamBefore, fixture.Cdn.RequestCount, "播放请求应完全命中预取缓存，不应再打上游");
            Assert.True(
                fixture.Proxy.Cache.GetStatistics().HitBytes >= hitBefore + BlockSize,
                "应记到命中字节");

            var stats = fixture.Prefetch.GetStatistics();
            Assert.True(stats.RequestedBytes > 0, "应记到请求字节");
            Assert.True(stats.ReceivedBytes > 0, "应记到实得字节");
            Assert.True(stats.UsefulHitBytes >= BlockSize, "应记到有效命中字节，实际 " + stats.UsefulHitBytes);
        });

        TestHost.Add("预取：阶段推进 3s → 10s → 30s，目标窗口递增且只补缺失块", async () =>
        {
            using var fixture = PrefetchFixture.Start(16 * 1024 * 1024);
            var target = fixture.RegisterPrefetchTarget("BV1prefetchStages");

            var immediatePlan = fixture.Prefetch.BuildPlan(target, MediaPrefetchStage.Immediate);
            var shortPlan = fixture.Prefetch.BuildPlan(target, MediaPrefetchStage.Short);
            var extendedPlan = fixture.Prefetch.BuildPlan(target, MediaPrefetchStage.Extended);
            Assert.True(immediatePlan.TargetBytes < shortPlan.TargetBytes, "10s 窗口应大于 3s");
            Assert.True(shortPlan.TargetBytes < extendedPlan.TargetBytes, "30s 窗口应大于 10s");

            var immediate = await fixture.Prefetch.PrefetchAsync(target, MediaPrefetchStage.Immediate, 0, CancellationToken.None);
            Assert.True(immediate.Success, "阶段一失败：" + immediate.Note);
            Assert.Equal(3, target.CompletedStageSeconds, "阶段一完成的秒数");

            var shortOutcome = await fixture.Prefetch.PrefetchAsync(target, MediaPrefetchStage.Short, 0, CancellationToken.None);
            Assert.True(shortOutcome.Success, "阶段二失败：" + shortOutcome.Note);
            Assert.Equal(10, target.CompletedStageSeconds, "阶段二完成的秒数");
            Assert.True(
                shortOutcome.AlreadyCachedBytes > 0,
                "阶段二应有一部分是阶段一留下的缓存，实际命中 " + shortOutcome.AlreadyCachedBytes);
            Assert.True(
                shortOutcome.ReceivedBytes < shortPlan.TargetBytes,
                "阶段二只应补缺失部分（实得 " + shortOutcome.ReceivedBytes + " / 目标 " + shortPlan.TargetBytes + "）");

            var extended = await fixture.Prefetch.PrefetchAsync(target, MediaPrefetchStage.Extended, 0, CancellationToken.None);
            Assert.True(extended.Success, "阶段三失败：" + extended.Note);
            Assert.Equal(30, target.CompletedStageSeconds, "阶段三完成的秒数");

            // 再来一次同样的阶段三：应该全部命中、一个字节都不用拉.
            var again = await fixture.Prefetch.PrefetchAsync(target, MediaPrefetchStage.Extended, 0, CancellationToken.None);
            Assert.Equal(0L, again.ReceivedBytes, "重复阶段三不应再拉数据");
            Assert.True(again.HitRate > 0.99, "重复阶段三命中率应接近 1，实际 " + again.HitRate);
        });

        TestHost.Add("预取：取消能停（不再继续发请求）", async () =>
        {
            using var fixture = PrefetchFixture.Start(32 * 1024 * 1024, responseDelayMilliseconds: 120);
            var target = fixture.RegisterPrefetchTarget("BV1prefetchCancel");

            using var cts = new CancellationTokenSource();
            var task = fixture.Prefetch.PrefetchSecondsAsync(target, 30, cts.Token);
            await Task.Delay(300);
            cts.Cancel();
            var outcome = await task;

            Assert.True(outcome.Cancelled, "应返回取消结果，实际 Note=" + outcome.Note);
            var afterCancel = fixture.Cdn.RequestCount;
            await Task.Delay(600);
            Assert.True(
                fixture.Cdn.RequestCount <= afterCancel + 1,
                $"取消后不应继续发请求，取消时 {afterCancel}，600ms 后 {fixture.Cdn.RequestCount}");

            var stats = fixture.Prefetch.GetStatistics();
            Assert.True(stats.CancelledStages >= 1, "应记到取消阶段，实际 " + stats.CancelledStages);
        });

        TestHost.Add("预取：超过预算按 LRU 淘汰最冷目标", async () =>
        {
            using var fixture = PrefetchFixture.Start(16 * 1024 * 1024, budgetBytes: 1024 * 1024);
            var first = fixture.RegisterPrefetchTarget("BV1budgetA");
            var second = fixture.RegisterPrefetchTarget("BV1budgetB");

            var outcomeA = await fixture.Prefetch.PrefetchAsync(first, MediaPrefetchStage.Immediate, 0, CancellationToken.None);
            Assert.True(outcomeA.Success, "第一个目标应成功：" + outcomeA.Note);
            Assert.True(fixture.Prefetch.Targets.Any(p => p.Id == first.Id), "第一个目标应还在");

            var outcomeB = await fixture.Prefetch.PrefetchAsync(second, MediaPrefetchStage.Immediate, 0, CancellationToken.None);
            Assert.True(outcomeB.Success, "第二个目标应成功（先淘汰旧的）：" + outcomeB.Note);

            var stats = fixture.Prefetch.GetStatistics();
            Assert.True(stats.EvictionCount >= 1, "应发生预算淘汰，实际 " + stats.EvictionCount);
            Assert.True(stats.WastedBytes > 0, "被淘汰且没播放用到的字节应记成浪费，实际 " + stats.WastedBytes);
            Assert.True(
                stats.TrackedBytes <= stats.BudgetBytes,
                $"存活目标占用不应超过预算：{stats.TrackedBytes} > {stats.BudgetBytes}");
            Assert.True(
                !fixture.Prefetch.Targets.Any(p => p.Id == first.Id),
                "最冷的目标应已被淘汰");
        });

        TestHost.Add("预取：全局并发上限生效（默认 2）", async () =>
        {
            using var fixture = PrefetchFixture.Start(32 * 1024 * 1024, responseDelayMilliseconds: 100, maxConcurrent: 2);
            var first = fixture.RegisterPrefetchTarget("BV1concurrentA");
            var second = fixture.RegisterPrefetchTarget("BV1concurrentB");

            await Task.WhenAll(
                fixture.Prefetch.PrefetchSecondsAsync(first, 30, CancellationToken.None),
                fixture.Prefetch.PrefetchSecondsAsync(second, 30, CancellationToken.None));

            Assert.True(fixture.Cdn.MaxConcurrentRequests > 0, "假 CDN 应记录到并发");
            Assert.True(
                fixture.Cdn.MaxConcurrentRequests <= 2,
                "上游并发不应超过 2，实际 " + fixture.Cdn.MaxConcurrentRequests);
        });

        TestHost.Add("预取：防抖 500ms——快速滚动期间不发起，稳定后只吐一次且按优先级排序", async () =>
        {
            using var gate = new MediaPrefetchPriorityGate(TimeSpan.FromMilliseconds(500));
            var events = new List<IReadOnlyList<MediaPrefetchCandidate>>();
            gate.Stabilized += list =>
            {
                lock (events)
                {
                    events.Add(list);
                }
            };

            // 模拟快速滚动：每 100ms 换一批候选，连续 1 秒.
            for (var i = 0; i < 10; i++)
            {
                gate.Submit(
                [
                    new MediaPrefetchCandidate { VideoId = "BV1scroll" + i, Title = "滚动中", VisibleRatio = 0.8, Index = i },
                ]);
                await Task.Delay(100);
                lock (events)
                {
                    Assert.Equal(0, events.Count, "滚动过程中不应触发预取");
                }
            }

            await Task.Delay(800);
            lock (events)
            {
                Assert.Equal(1, events.Count, "稳定后只应触发一次");
            }

            // 优先级：悬停 / 焦点排最前，其次可见面积，再按停留时间.
            var ordered = MediaPrefetchPriorityGate.Sort(
            [
                new MediaPrefetchCandidate { VideoId = "next", VisibleRatio = 0, IsNextScreen = true, Index = 5 },
                new MediaPrefetchCandidate { VideoId = "small", VisibleRatio = 0.2, Dwell = TimeSpan.FromSeconds(9), Index = 2 },
                new MediaPrefetchCandidate { VideoId = "hover", VisibleRatio = 0.1, IsHovered = true, Index = 3 },
                new MediaPrefetchCandidate { VideoId = "big", VisibleRatio = 0.9, Dwell = TimeSpan.FromSeconds(1), Index = 1 },
                new MediaPrefetchCandidate { VideoId = "focus", VisibleRatio = 0.05, IsFocused = true, Index = 4 },
            ]);

            Assert.Equal("hover", ordered[0].VideoId, "悬停优先");
            Assert.Equal("focus", ordered[1].VideoId, "焦点其次（同为最高优先级时按可见面积排）");
            Assert.Equal("big", ordered[2].VideoId, "然后才是可见面积最大的");
            Assert.Equal("small", ordered[3].VideoId, "可见面积小的靠后");
            Assert.Equal("next", ordered[4].VideoId, "下一屏最后");
        });

        TestHost.Add("预取：SIDX 解析把时间精确换算成字节", () =>
        {
            var payload = BuildSidx(out var mediaBytes);
            var index = MediaSegmentIndex.TryParse(payload, out var error);
            Assert.NotNull(index, "SIDX 应能解析：" + error);
            Assert.Equal(4.0, index!.TotalDurationSeconds, "总时长（秒）");
            Assert.Equal(3, index.ReferenceCount, "引用段数");
            Assert.Equal(1000L, index.GetByteLengthForSeconds(1), "1 秒对应 1000 字节");
            Assert.Equal(3000L, index.GetByteLengthForSeconds(1.5), "1.5 秒对齐到第 2 段结尾");
            Assert.Equal(mediaBytes, index.GetByteLengthForSeconds(99), "超出总时长按全段算");

            Assert.True(index.TryMapTime(1.5, 500, out var start, out var length), "应能把时间映到子段");
            Assert.Equal(1500L, start, "子段绝对起点");
            Assert.Equal(2000L, length, "子段长度");

            var bogus = MediaSegmentIndex.TryParse(new byte[] { 1, 2, 3, 4, 5 }, out var bogusError);
            Assert.True(bogus is null, "垃圾数据应解析失败");
            Assert.True(!string.IsNullOrEmpty(bogusError), "失败应给出原因");
        });

        TestHost.Add("预取：有 SIDX 时按精确字节算（非估算），没有时才标估算", async () =>
        {
            using var fixture = PrefetchFixture.Start(4 * 1024 * 1024);
            var info = new MediaPrefetchSourceInfo
            {
                DisplayName = "SIDX 目标",
                BandwidthBps = TwoMegabitsPerSecond,
                InitializationRange = new MediaByteRange(0, 200),
                IndexRange = new MediaByteRange(200, 400),
            };
            var target = fixture.RegisterPrefetchTarget("BV1sidxtarget", info);

            var estimatePlan = fixture.Prefetch.BuildPlan(target, MediaPrefetchStage.Immediate);
            Assert.True(estimatePlan.IsEstimate, "没有 SIDX 时应标估算");
            Assert.Equal("bandwidth", estimatePlan.Source, "估算依据应是标称码率");

            var sidxBytes = BuildSidx(out _);
            var parsed = MediaSegmentIndex.TryParse(sidxBytes, out _);
            Assert.NotNull(parsed, "SIDX 应能解析");
            target.SegmentIndex = parsed;

            var exactPlan = fixture.Prefetch.BuildPlan(target, MediaPrefetchStage.Immediate);
            Assert.False(exactPlan.IsEstimate, "有 SIDX 时不应再标估算");
            Assert.Equal("sidx", exactPlan.Source, "换算依据应是 SIDX");
            Assert.True(
                exactPlan.TargetBytes < estimatePlan.TargetBytes,
                $"精确窗口应小于按码率估的窗口（{exactPlan.TargetBytes} vs {estimatePlan.TargetBytes}）");
            // 缓存按整块存，所以窗口会向外对齐到块边界；只要覆盖到 init + index + SIDX 前 3 秒就算对.
            Assert.True(exactPlan.Windows.Count >= 1, "应有目标窗口");
            Assert.Equal(0L, exactPlan.Windows[0].Start, "窗口应从 init 段（文件开头）起");
            Assert.True(
                exactPlan.Windows[0].EndExclusive >= 600 + parsed!.GetByteLengthForSeconds(3),
                "窗口应覆盖 init + index + SIDX 前 3 秒");
        });

        TestHost.Add("预取：按时间点预取（E 的空降）落在后半段字节上", async () =>
        {
            using var fixture = PrefetchFixture.Start(8 * 1024 * 1024, responseDelayMilliseconds: 0);
            var info = new MediaPrefetchSourceInfo
            {
                DisplayName = "空降目标",
                BandwidthBps = TwoMegabitsPerSecond,
                InitializationRange = new MediaByteRange(0, 100),
                IndexRange = new MediaByteRange(100, 300),
                DurationSeconds = 33,
            };
            var target = fixture.RegisterPrefetchTarget("BV1seektarget", info);

            var outcome = await fixture.Prefetch.PrefetchAtTimeAsync(
                target,
                TimeSpan.FromSeconds(27),
                windowSeconds: 4,
                CancellationToken.None);

            Assert.True(outcome.Success, "时间点预取应成功：" + outcome.Note);
            Assert.True(outcome.TargetBytes > 0, "应算出目标窗口");

            var present = target.Registration.Lease.GetPresentRanges(
                new MediaByteRange(0, target.Registration.Lease.Length ?? 0));
            Assert.True(present.Count > 0, "应有字节进了缓存");

            // 拉 index 段会顺带缓存文件开头那一块，所以看「有没有落在后半段的缓存」，而不是第一段的起点.
            Assert.True(
                present.Any(p => p.Start > 1024 * 1024),
                "空降点 27s 的字节应落在文件后半段，实际缓存区间 [" +
                string.Join(", ", present.Select(p => p.ToString())) + "]");
        });

        TestHost.Add("预取：统计含请求 / 实得 / 命中 / 浪费 / 条目数", async () =>
        {
            using var fixture = PrefetchFixture.Start(8 * 1024 * 1024);
            var target = fixture.RegisterPrefetchTarget("BV1stats");
            var outcome = await fixture.Prefetch.PrefetchAsync(target, MediaPrefetchStage.Short, 0, CancellationToken.None);
            Assert.True(outcome.Success, "预取应成功：" + outcome.Note);

            var stats = fixture.Prefetch.GetStatistics();
            Assert.True(stats.RequestedBytes > 0, "请求字节应 > 0");
            Assert.True(stats.ReceivedBytes > 0, "实得字节应 > 0");
            Assert.True(stats.RequestedBytes >= stats.ReceivedBytes, "请求字节应 >= 实得字节");
            Assert.True(stats.TrackedBytes > 0, "存活目标占用应 > 0");
            Assert.True(stats.ActiveTargets >= 1, "存活目标数应 >= 1");
            Assert.True(stats.EntryCount >= 1, "缓存条目数应 >= 1");
            Assert.True(stats.BudgetBytes == 256L * 1024 * 1024, "默认预算 256 MiB");
            Console.WriteLine("        统计样例：" + stats);
        });
    }

    private static HttpClient CreateClient()
        => new(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<byte[]> GetRangeAsync(HttpClient client, string url, long start, long end)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>
    /// 造一段 SIDX 字节：3 个引用段（1000/2000/3000 字节，1s/1s/2s）.
    /// </summary>
    /// <param name="mediaBytes">总媒体字节.</param>
    /// <returns>SIDX 盒.</returns>
    internal static byte[] BuildSidx(out long mediaBytes)
    {
        const int Count = 3;
        var boxSize = 8 + 4 + 4 + 4 + 4 + 4 + 2 + 2 + (Count * 12);
        var buffer = new byte[boxSize];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)boxSize);
        Encoding.ASCII.GetBytes("sidx").CopyTo(buffer, 4);
        var pos = 8;
        buffer[pos] = 0; // version 0
        pos += 4; // version + flags
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), 1); // reference_ID
        pos += 4;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), 1000); // timescale
        pos += 4;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), 0); // earliest_presentation_time
        pos += 4;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), 0); // first_offset
        pos += 4;
        pos += 2; // reserved
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos, 2), Count);
        pos += 2;

        long[] sizes = [1000, 2000, 3000];
        uint[] durations = [1000, 1000, 2000];
        for (var i = 0; i < Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), (uint)sizes[i]);
            pos += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), durations[i]);
            pos += 4;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), 0);
            pos += 4;
        }

        mediaBytes = sizes.Sum();
        return buffer;
    }
}

/// <summary>
/// 假 CDN：支持 Range、可配延迟，并记录「同时最多几个请求在飞」。
/// </summary>
internal sealed class PrefetchCdn : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _payload;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<string> _ranges = [];
    private int _requestCount;
    private int _inFlight;
    private int _maxInFlight;
    private int _disposed;

    public PrefetchCdn(byte[] payload, string path, int responseDelayMilliseconds)
    {
        _payload = payload;
        ResponseDelayMilliseconds = responseDelayMilliseconds;
        Port = GetFreePort();
        _listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/"));
        _listener.Start();
        Url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}{path}");
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public string Url { get; }

    public byte[] Payload => _payload;

    public int ResponseDelayMilliseconds { get; set; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    public int MaxConcurrentRequests => Volatile.Read(ref _maxInFlight);

    public IReadOnlyList<string> RangeHeaders
    {
        get
        {
            lock (_gate)
            {
                return [.. _ranges];
            }
        }
    }

    public void ClearRecorded()
    {
        lock (_gate)
        {
            _ranges.Clear();
        }

        Volatile.Write(ref _maxInFlight, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            Interlocked.Increment(ref _requestCount);
            var current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, current);
            try
            {
                var rangeHeader = context.Request.Headers["Range"];
                lock (_gate)
                {
                    _ranges.Add(string.IsNullOrEmpty(rangeHeader) ? "none" : rangeHeader);
                }

                if (ResponseDelayMilliseconds > 0)
                {
                    await Task.Delay(ResponseDelayMilliseconds);
                }

                var total = _payload.Length;
                if (string.IsNullOrEmpty(rangeHeader))
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength64 = total;
                    context.Response.ContentType = "video/mp4";
                    await context.Response.OutputStream.WriteAsync(_payload);
                    context.Response.Close();
                    return;
                }

                if (!TryParseRange(rangeHeader, total, out var start, out var end))
                {
                    context.Response.StatusCode = 416;
                    context.Response.AddHeader("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes */{total}"));
                    context.Response.ContentLength64 = 0;
                    context.Response.Close();
                    return;
                }

                var length = end - start + 1;
                context.Response.StatusCode = 206;
                context.Response.ContentType = "video/mp4";
                context.Response.AddHeader("Accept-Ranges", "bytes");
                context.Response.AddHeader("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{end}/{total}"));
                context.Response.ContentLength64 = length;
                await context.Response.OutputStream.WriteAsync(_payload.AsMemory((int)start, (int)length));
                context.Response.Close();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        while (value > (snapshot = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot)
            {
                return;
            }
        }
    }

    private static bool TryParseRange(string header, int total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        var text = header.Trim();
        if (!text.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = text[6..];
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return false;
        }

        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];
        if (startText.Length == 0)
        {
            return false;
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out start))
        {
            return false;
        }

        if (endText.Length > 0 && long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            end = parsed;
        }

        if (start >= total)
        {
            return false;
        }

        end = Math.Min(end, total - 1);
        return end >= start;
    }
}

/// <summary>
/// 预取测试夹具：真代理 + 假 CDN + 预取服务.
/// </summary>
internal sealed class PrefetchFixture : IDisposable
{
    private PrefetchFixture(MediaBufferProxy proxy, PrefetchCdn cdn, MediaPrefetchService prefetch)
    {
        Proxy = proxy;
        Cdn = cdn;
        Prefetch = prefetch;
    }

    public MediaBufferProxy Proxy { get; }

    public PrefetchCdn Cdn { get; }

    public MediaPrefetchService Prefetch { get; }

    public static PrefetchFixture Start(
        int payloadSize,
        int responseDelayMilliseconds = 0,
        long budgetBytes = 256L * 1024 * 1024,
        int maxConcurrent = 2)
    {
        var payload = FakeCdn.CreatePayload(payloadSize);
        var cdn = new PrefetchCdn(payload, "/upgcxcode/prefetch/test.m4s", responseDelayMilliseconds);
        var proxy = new MediaBufferProxy(new MediaProxyOptions
        {
            BlockSize = 256 * 1024,
            CapacityBytes = 64L * 1024 * 1024,
            SessionToken = "prefetchtoken0123456789abcdef",
            UpstreamTimeout = TimeSpan.FromSeconds(20),
        })
        {
            Log = (level, message) =>
            {
                if (level >= MediaProxyLogLevel.Warning)
                {
                    Console.WriteLine($"        [proxy:{level}] {message}");
                }
            },
        };
        proxy.Start();

        var prefetch = new MediaPrefetchService(proxy, new MediaPrefetchOptions
        {
            MaxConcurrent = maxConcurrent,
            BudgetBytes = budgetBytes,
            RequestTimeout = TimeSpan.FromSeconds(20),
        })
        {
            Log = (level, message) =>
            {
                if (level >= MediaProxyLogLevel.Information)
                {
                    Console.WriteLine("        [prefetch:" + level + "] " + message);
                }
            },
        };

        return new PrefetchFixture(proxy, cdn, prefetch);
    }

    public MediaStreamIdentity CreateIdentity(string bvid)
        => MediaStreamIdentity.Create(
            bvid,
            "9001",
            "80",
            "7",
            "video/mp4",
            2_000_000,
            null,
            null,
            Cdn.Url + "?deadline=1&upsig=x");

    public MediaStreamSourceDescriptor CreateDescriptor(string bvid, string ownerKey)
        => new(Cdn.Url, CreateIdentity(bvid))
        {
            Cookie = "SESSDATA=fake",
            Referer = "https://www.bilibili.com",
            UserAgent = "MediaCache.Tests/1.0",
            DisplayName = bvid,
            OwnerKey = ownerKey,
        };

    public MediaPrefetchTarget RegisterPrefetchTarget(string bvid, MediaPrefetchSourceInfo? info = null)
    {
        return Prefetch.RegisterTarget(CreateDescriptor(bvid, "page-" + bvid), info ?? new MediaPrefetchSourceInfo
        {
            DisplayName = bvid,
            BandwidthBps = 2_000_000,
        });
    }

    /// <summary>
    /// 模拟播放侧登记：归属键不同（不会被预取释放），身份与预取目标相同（共享同一个缓存条目）.
    /// </summary>
    /// <param name="bvid">与预取目标一致的视频 id.</param>
    /// <returns>播放登记.</returns>
    public MediaProxyRegistration RegisterPlayback(string bvid)
        => Proxy.Register(new MediaStreamSourceDescriptor(Cdn.Url, CreateIdentity(bvid))
        {
            Cookie = "SESSDATA=fake",
            Referer = "https://www.bilibili.com",
            UserAgent = "MediaCache.Tests/1.0",
            DisplayName = "播放",
            OwnerKey = "playback",
        });

    public void Dispose()
    {
        Prefetch.Dispose();
        Proxy.Dispose();
        Cdn.Dispose();
    }
}
