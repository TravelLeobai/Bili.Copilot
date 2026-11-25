// Copyright (c) Bili Copilot. All rights reserved.

using System.Net.Http.Headers;
using BiliCopilot.UI.Services.Media;
using MediaCache.Tests;

Console.WriteLine("== 媒体字节范围缓存 + 仅回环 Range 代理 · 测试 ==");
Console.WriteLine();

RegisterRangeTests();
RegisterCacheTests();
RegisterProxyTests();
FfmpegE2ETest.Register();

var filter = args.Length > 0 ? args[0] : null;
return await TestHost.RunAsync(filter);

static void RegisterRangeTests()
{
    TestHost.Add("Range 解析：bytes=0-99 于 1000 长度", () =>
    {
        var result = MediaByteRanges.TryParse("bytes=0-99", 1000, out var range);
        Assert.Equal(MediaRangeParseResult.Valid, result, "解析结果");
        Assert.Equal(0L, range.Start, "起点");
        Assert.Equal(100L, range.Length, "长度");
    });

    TestHost.Add("Range 解析：bytes=100- 到结尾", () =>
    {
        var result = MediaByteRanges.TryParse("bytes=100-", 1000, out var range);
        Assert.Equal(MediaRangeParseResult.Valid, result, "解析结果");
        Assert.Equal(100L, range.Start, "起点");
        Assert.Equal(900L, range.Length, "长度");
    });

    TestHost.Add("Range 解析：bytes=-100 取最后 100 字节", () =>
    {
        var result = MediaByteRanges.TryParse("bytes=-100", 1000, out var range);
        Assert.Equal(MediaRangeParseResult.Valid, result, "解析结果");
        Assert.Equal(900L, range.Start, "起点");
        Assert.Equal(100L, range.Length, "长度");
    });

    TestHost.Add("Range 解析：尾部溢出按总长度裁剪", () =>
    {
        var result = MediaByteRanges.TryParse("bytes=500-100000", 1000, out var range);
        Assert.Equal(MediaRangeParseResult.Valid, result, "解析结果");
        Assert.Equal(500L, range.Start, "起点");
        Assert.Equal(500L, range.Length, "长度");
    });

    TestHost.Add("Range 解析：起点越界 → Unsatisifiable（应回 416）", () =>
    {
        var result = MediaByteRanges.TryParse("bytes=1000-", 1000, out _);
        Assert.Equal(MediaRangeParseResult.Unsatisfiable, result, "解析结果");
    });

    TestHost.Add("Range 解析：语法错误 / 多段 → Unsupported", () =>
    {
        Assert.Equal(MediaRangeParseResult.Unsupported, MediaByteRanges.TryParse("bytes=abc", 1000, out _), "语法错误");
        Assert.Equal(MediaRangeParseResult.Unsupported, MediaByteRanges.TryParse("bytes=0-9,20-29", 1000, out _), "多段");
        Assert.Equal(MediaRangeParseResult.Unsupported, MediaByteRanges.TryParse("items=0-9", 1000, out _), "单位错误");
    });

    TestHost.Add("Range 解析：没有 Range 头 → None", () =>
    {
        Assert.Equal(MediaRangeParseResult.None, MediaByteRanges.TryParse(null, 1000, out _), "null");
        Assert.Equal(MediaRangeParseResult.None, MediaByteRanges.TryParse("  ", 1000, out _), "空白");
    });

    TestHost.Add("范围合并：重叠与首尾相接合成一段", () =>
    {
        var merged = MediaByteRanges.Merge([
            new MediaByteRange(100, 50),
            new MediaByteRange(0, 50),
            new MediaByteRange(50, 50),
            new MediaByteRange(500, 10),
        ]);

        Assert.Equal(2, merged.Count, "合并段数");
        Assert.Equal(0L, merged[0].Start, "第一段起点");
        Assert.Equal(150L, merged[0].Length, "第一段长度");
        Assert.Equal(500L, merged[1].Start, "第二段起点");
    });

    TestHost.Add("范围求差：只报缺失段", () =>
    {
        var missing = MediaByteRanges.Subtract(
            [new MediaByteRange(0, 256), new MediaByteRange(512, 256)],
            new MediaByteRange(0, 1000));

        Assert.Equal(2, missing.Count, "缺失段数");
        Assert.Equal(256L, missing[0].Start, "缺失段 1 起点");
        Assert.Equal(256L, missing[0].Length, "缺失段 1 长度");
        Assert.Equal(768L, missing[1].Start, "缺失段 2 起点");
        Assert.Equal(232L, missing[1].Length, "缺失段 2 长度");
    });

    TestHost.Add("范围求交：裁剪到窗口内", () =>
    {
        var present = MediaByteRanges.Intersect(
            [new MediaByteRange(0, 256), new MediaByteRange(512, 256)],
            new MediaByteRange(100, 500));

        Assert.Equal(2, present.Count, "命中段数");
        Assert.Equal(100L, present[0].Start, "命中段 1 起点");
        Assert.Equal(156L, present[0].Length, "命中段 1 长度（被窗口右边界裁到 256）");
        Assert.Equal(512L, present[1].Start, "命中段 2 起点");
        Assert.Equal(88L, present[1].Length, "命中段 2 长度（窗口只到 600）");
    });

    TestHost.Add("身份指纹：签名/主机变化不改变指纹，路径不同才算不同", () =>
    {
        var a = MediaStreamIdentity.ComputeFingerprint("https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/1/2/3.m4s?deadline=111&upsig=aaa");
        var b = MediaStreamIdentity.ComputeFingerprint("https://upos-sz-mirrorhwo1.bilivideo.com/upgcxcode/1/2/3.m4s?deadline=999&upsig=bbb");
        var c = MediaStreamIdentity.ComputeFingerprint("https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/4/5/6.m4s?deadline=111&upsig=aaa");
        Assert.Equal(a, b, "同路径不同签名");
        Assert.True(a != c, "不同路径指纹应不同");
    });

    TestHost.Add("身份等价性：逻辑字段任一不同即不等价", () =>
    {
        var baseIdentity = MediaStreamIdentity.Create("BV1", "100", "80", "7", "video/mp4", 1000, null, null, "https://a/x/y.m4s?a=1");
        var renamedSignature = MediaStreamIdentity.Create("BV1", "100", "80", "7", "video/mp4", 1000, null, null, "https://z/x/y.m4s?a=2");
        var otherQuality = MediaStreamIdentity.Create("BV1", "100", "64", "7", "video/mp4", 1000, null, null, "https://a/x/y.m4s?a=1");
        Assert.True(baseIdentity.Equals(renamedSignature), "签名变化应等价");
        Assert.False(baseIdentity.Equals(otherQuality), "换画质不应等价");
    });
}

static void RegisterCacheTests()
{
    const int BlockSize = 1024;
    const long Capacity = 8 * BlockSize;

    TestHost.Add("缓存：整块写入后命中，半块拒绝写入", () =>
    {
        var cache = new MediaRangeCache(Capacity, BlockSize);
        var lease = cache.AcquireLease(Identity("cache-basic"));
        lease.Length = 4 * BlockSize;

        var fullBlock = CreateBytes(BlockSize, 1);
        Assert.True(lease.TryWriteBlock(0, fullBlock), "整块写入应当成功");
        Assert.True(lease.TryGetBlock(0, out var read), "应当命中");
        Assert.Equal(BlockSize, read.Length, "块长度");
        Assert.True(read.AsSpan().SequenceEqual(fullBlock), "块内容应一致");

        Assert.False(lease.TryWriteBlock(1, new byte[BlockSize / 2]), "半块不应写入");
        Assert.False(lease.ContainsBlock(1), "半块不应算命中");
        lease.Dispose();
    });

    TestHost.Add("缓存：部分命中，缺失段只报没缓存的那些", () =>
    {
        var cache = new MediaRangeCache(Capacity, BlockSize);
        var lease = cache.AcquireLease(Identity("cache-partial"));
        lease.Length = 4 * BlockSize;
        lease.TryWriteBlock(0, CreateBytes(BlockSize, 2));
        lease.TryWriteBlock(2, CreateBytes(BlockSize, 3));

        var window = new MediaByteRange(0, 4 * BlockSize);
        var present = lease.GetPresentRanges(window);
        var missing = lease.GetMissingRanges(window);

        Assert.Equal(2, present.Count, "命中段数");
        Assert.Sequence([0, 2 * BlockSize], [present[0].Start, present[1].Start], "命中段起点");
        Assert.Equal(2, missing.Count, "缺失段数");
        Assert.Sequence([BlockSize, 3 * BlockSize], [missing[0].Start, missing[1].Start], "缺失段起点");
        lease.Dispose();
    });

    TestHost.Add("缓存：LRU 按最久未访问淘汰", () =>
    {
        var cache = new MediaRangeCache(2 * BlockSize, BlockSize);
        var leaseA = cache.AcquireLease(Identity("lru-a"));
        var leaseB = cache.AcquireLease(Identity("lru-b"));
        leaseA.Length = BlockSize;
        leaseB.Length = BlockSize;
        leaseA.TryWriteBlock(0, CreateBytes(BlockSize, 10));
        leaseB.TryWriteBlock(0, CreateBytes(BlockSize, 11));
        leaseA.Dispose();
        leaseB.Dispose();

        // 再写第三个条目：容量只够两块，最久没访问的 A 应被淘汰.
        var leaseC = cache.AcquireLease(Identity("lru-c"));
        leaseC.Length = BlockSize;
        Assert.True(leaseC.TryWriteBlock(0, CreateBytes(BlockSize, 12)), "第三次写入应成功");

        var leaseA2 = cache.AcquireLease(Identity("lru-a"));
        Assert.False(leaseA2.TryGetBlock(0, out _), "A 应被淘汰");
        var leaseB2 = cache.AcquireLease(Identity("lru-b"));
        Assert.True(leaseB2.TryGetBlock(0, out _), "B 应还在");

        var stats = cache.GetStatistics();
        Assert.True(stats.EvictionCount >= 1, "应有淘汰记录");
        Assert.True(stats.EvictedBytes >= BlockSize, "淘汰字节应 >= 一块");
        leaseA2.Dispose();
        leaseB2.Dispose();
        leaseC.Dispose();
    });

    TestHost.Add("缓存：租约阻止淘汰（宁可不写也不丢在播的数据）", () =>
    {
        var cache = new MediaRangeCache(BlockSize, BlockSize);
        var leaseA = cache.AcquireLease(Identity("lease-a"));
        leaseA.Length = BlockSize;
        Assert.True(leaseA.TryWriteBlock(0, CreateBytes(BlockSize, 20)), "A 写入应成功");

        var leaseB = cache.AcquireLease(Identity("lease-b"));
        leaseB.Length = BlockSize;
        Assert.False(leaseB.TryWriteBlock(0, CreateBytes(BlockSize, 21)), "没地方了应当写入失败");

        leaseA.TryGetBlock(0, out var kept);
        Assert.Equal(BlockSize, kept.Length, "A 的数据应完好");
        var stats = cache.GetStatistics();
        Assert.True(stats.CurrentBytes <= stats.CapacityBytes, "占用不应超过上限");
        leaseA.Dispose();
        leaseB.Dispose();
    });

    TestHost.Add("缓存：容量上限硬约束（大量写入后不超预算）", () =>
    {
        var cache = new MediaRangeCache(4 * BlockSize, BlockSize);
        for (var i = 0; i < 10; i++)
        {
            var lease = cache.AcquireLease(Identity("budget-" + i));
            lease.Length = 2 * BlockSize;
            lease.TryWriteBlock(0, CreateBytes(BlockSize, i));
            lease.TryWriteBlock(1, CreateBytes(BlockSize, i + 50));
            lease.Dispose();
        }

        var stats = cache.GetStatistics();
        Assert.True(stats.CurrentBytes <= stats.CapacityBytes, "当前占用不应超过上限");
        Assert.True(stats.EntryCount <= 4, "条目数应被限制");
    });

    TestHost.Add("缓存：命中/未命中字节统计", () =>
    {
        var cache = new MediaRangeCache(Capacity, BlockSize);
        var lease = cache.AcquireLease(Identity("stats"));
        lease.MarkServedFromCache(1000);
        lease.MarkFetchedFromUpstream(500);
        lease.Dispose();

        var stats = cache.GetStatistics();
        Assert.Equal(1000L, stats.HitBytes, "命中字节");
        Assert.Equal(500L, stats.MissBytes, "未命中字节");
    });
}

static void RegisterProxyTests()
{
    const int PayloadSize = 1024 * 1024;
    const int BlockSize = 256 * 1024;

    TestHost.Add("代理：非法 token / 未知 id / 非 /m/ 路径一律 404（含试图转发 URL）", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();

        var badToken = await client.GetAsync(new Uri($"{fixture.BaseUrl}m/deadbeef/{fixture.Registration.Id}"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, badToken.StatusCode, "错误 token");

        var unknownId = await client.GetAsync(new Uri($"{fixture.BaseUrl}m/{fixture.Proxy.SessionToken}/0123456789abcdef0123456789abcdef"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknownId.StatusCode, "未知 id");

        var forwardAttempt = await client.GetAsync(new Uri($"{fixture.BaseUrl}forward?url=http%3A%2F%2Fexample.com%2Fsecret"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, forwardAttempt.StatusCode, "非 /m/ 路径");

        var pathTraversal = await client.GetAsync(new Uri($"{fixture.BaseUrl}m/{fixture.Proxy.SessionToken}/..%2F..%2Fetc%2Fpasswd"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, pathTraversal.StatusCode, "路径穿越");
    });

    TestHost.Add("代理：206 + Content-Range/Content-Length 正确，字节与源一致", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();

        var (response, body) = await GetRangeAsync(client, fixture.Registration.ProxyUrl, 1000, 1999);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode, "状态码");
        Assert.Equal("bytes 1000-1999/1048576", response.Content.Headers.ContentRange?.ToString(), "Content-Range");
        Assert.Equal(1000L, response.Content.Headers.ContentLength, "Content-Length");
        Assert.Equal(1000, body.Length, "响应体长度");
        Assert.True(body.AsSpan().SequenceEqual(fixture.Cdn.Payload.AsSpan(1000, 1000)), "字节应与源一致");
        Assert.True(
            response.Headers.Contains("Accept-Ranges") || response.Content.Headers.Contains("Accept-Ranges"),
            "应有 Accept-Ranges");
    });

    TestHost.Add("代理：HEAD 返回长度与 Range 支持，不带体", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Head, fixture.Registration.ProxyUrl);
        using var response = await client.SendAsync(request);
        Assert.True(
            response.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.PartialContent,
            "HEAD 状态码应为 200/206，实际 " + (int)response.StatusCode);
        Assert.Equal((long)PayloadSize, response.Content.Headers.ContentLength, "HEAD 应给出总长度");
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(0, body.Length, "HEAD 不应有响应体");
    });

    TestHost.Add("代理：越界 Range 返回 416 + bytes */总长", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();

        var (response, _) = await GetRangeAsync(client, fixture.Registration.ProxyUrl, PayloadSize, null);
        Assert.Equal(System.Net.HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode, "状态码");
        Assert.Equal("bytes */1048576", response.Content.Headers.ContentRange?.ToString(), "Content-Range");
    });

    TestHost.Add("代理：第二次请求同一 range 不再打上游（缓存复用）", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();

        var (firstResponse, firstBody) = await GetRangeAsync(client, fixture.Registration.ProxyUrl, 0, BlockSize - 1);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, firstResponse.StatusCode, "第一次状态码");
        var upstreamAfterFirst = fixture.Cdn.RequestCount;
        Assert.True(upstreamAfterFirst > 0, "第一次应打到上游，实际 " + upstreamAfterFirst);

        var (secondResponse, secondBody) = await GetRangeAsync(client, fixture.Registration.ProxyUrl, 0, BlockSize - 1);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, secondResponse.StatusCode, "第二次状态码");
        Assert.Equal(upstreamAfterFirst, fixture.Cdn.RequestCount, "第二次不应再打上游");
        Assert.True(firstBody.AsSpan().SequenceEqual(secondBody), "两次响应体应一致");

        var stats = fixture.Proxy.GetStatistics();
        Assert.True(stats.Cache.HitBytes >= BlockSize, "应记到命中字节，实际 " + stats.Cache.HitBytes);
        Assert.True(stats.Cache.MissBytes >= BlockSize, "应记到未命中字节，实际 " + stats.Cache.MissBytes);
    });

    TestHost.Add("代理：部分命中只补缺失块（第 2 块已在缓存时不再拉第 2 块）", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();

        var (warmResponse, _) = await GetRangeAsync(client, fixture.Registration.ProxyUrl, 0, BlockSize - 1);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, warmResponse.StatusCode, "预热状态码");
        fixture.Cdn.ClearRecorded();

        var (response, body) = await GetRangeAsync(client, fixture.Registration.ProxyUrl, 0, (2 * BlockSize) - 1);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode, "状态码");
        Assert.Equal(2 * BlockSize, body.Length, "响应体长度");
        Assert.True(body.AsSpan().SequenceEqual(fixture.Cdn.Payload.AsSpan(0, 2 * BlockSize)), "字节应与源一致");

        var ranges = fixture.Cdn.RangeHeaders;
        Assert.Equal(1, ranges.Count, "只应当补 1 个缺失块，实际上游请求 [" + string.Join(" | ", ranges) + "]");
        Assert.Equal($"bytes={BlockSize}-{(2 * BlockSize) - 1}", ranges[0], "补的应当正好是缺失块");
    });

    TestHost.Add("代理：并发同一 range 只下一个 flight", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        fixture.Cdn.ResponseDelayMilliseconds = 250;
        using var client = CreateClient();

        var first = GetRangeAsync(client, fixture.Registration.ProxyUrl, 0, (2 * BlockSize) - 1);
        var second = GetRangeAsync(client, fixture.Registration.ProxyUrl, 0, (2 * BlockSize) - 1);
        var results = await Task.WhenAll(first, second);

        Assert.True(
            results[0].Response.StatusCode == System.Net.HttpStatusCode.PartialContent,
            "并发请求 1 状态码 " + (int)results[0].Response.StatusCode);
        Assert.True(
            results[1].Response.StatusCode == System.Net.HttpStatusCode.PartialContent,
            "并发请求 2 状态码 " + (int)results[1].Response.StatusCode);

        var ranges = fixture.Cdn.RangeHeaders;
        var block0 = ranges.Count(p => p == $"bytes=0-{BlockSize - 1}");
        var block1 = ranges.Count(p => p == $"bytes={BlockSize}-{(2 * BlockSize) - 1}");
        var probes = ranges.Count(p => p == "bytes=0-0");
        Assert.Equal(1, block0, "第 0 块只应下一次，实际上游记录 [" + string.Join(" | ", ranges) + "]");
        Assert.Equal(1, block1, "第 1 块只应下一次，实际上游记录 [" + string.Join(" | ", ranges) + "]");
        Assert.Equal(1, probes, "长度探测只应一次，实际上游记录 [" + string.Join(" | ", ranges) + "]");
    });

    TestHost.Add("代理：上游 403 退避重试后仍能取到数据", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();

        fixture.Cdn.FailNextRequests(1);
        var (response, body) = await GetRangeAsync(client, fixture.Registration.ProxyUrl, 0, 999);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode, "状态码（重试后应成功）");
        Assert.Equal(1000, body.Length, "响应体长度");

        var stats = fixture.Proxy.GetStatistics();
        Assert.True(stats.UpstreamRetries >= 1, "应记到至少一次重试，实际 " + stats.UpstreamRetries);
    });

    TestHost.Add("代理：重复登记同一条源会复用登记（不叠租约）", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        var same = fixture.Proxy.Register(new MediaStreamSourceDescriptor(fixture.Registration.Source.Url, fixture.Registration.Identity)
        {
            OwnerKey = "fixture",
            DisplayName = "重复登记",
        });
        Assert.Equal(fixture.Registration.Id, same.Id, "同 owner + 同身份 + 同 URL 应复用同一登记");
        Assert.Equal(1, fixture.Proxy.GetStatistics().ActiveRegistrations, "登记数不应增加");
    });

    TestHost.Add("代理：按归属键批量释放后，旧地址立即 404", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();

        var (warm, _) = await GetRangeAsync(client, fixture.Registration.ProxyUrl, 0, 999);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, warm.StatusCode, "预热状态码");

        var released = fixture.Proxy.ReleaseAllOwnedBy("fixture");
        Assert.Equal(1, released, "应按归属键释放 1 条登记");

        var after = await client.GetAsync(fixture.Registration.ProxyUrl);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, after.StatusCode, "释放后应 404");
        Assert.Equal(0, fixture.Proxy.GetStatistics().ActiveRegistrations, "登记应清空");
    });

    TestHost.Add("代理：Dispose 登记后地址失效", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        using var client = CreateClient();
        var url = fixture.Registration.ProxyUrl;
        fixture.Registration.Dispose();

        var response = await client.GetAsync(url);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode, "Dispose 后应 404");
    });

    TestHost.Add("代理：只绑回环，且能识别自己的地址", async () =>
    {
        using var fixture = await ProxyFixture.StartAsync(PayloadSize, BlockSize);
        Assert.True(fixture.Proxy.Port > 0, "应拿到动态端口");
        Assert.True(MediaBufferProxy.IsProxyUrl(fixture.Registration.ProxyUrl), "应识别为代理地址");
        Assert.False(MediaBufferProxy.IsProxyUrl(fixture.Cdn.Url), "上游地址不应被识别为代理地址");

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, fixture.Proxy.Port);
        Assert.True(tcp.Connected, "回环端口应可连接");
    });
}

static MediaStreamIdentity Identity(string tag)
    => MediaStreamIdentity.Create(
        "BV1test",
        "1001",
        tag,
        "7",
        "video/mp4",
        1_000_000,
        null,
        null,
        $"https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/aa/bb/{tag}.m4s?deadline=1&upsig=x");

static byte[] CreateBytes(int length, int seed)
{
    var data = new byte[length];
    for (var i = 0; i < length; i++)
    {
        data[i] = (byte)((i * 31) + seed);
    }

    return data;
}

static HttpClient CreateClient()
    => new(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };

static async Task<(HttpResponseMessage Response, byte[] Body)> GetRangeAsync(HttpClient client, string url, long start, long? end)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Range = new RangeHeaderValue(start, end);
    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    var body = await response.Content.ReadAsByteArrayAsync();
    return (response, body);
}

/// <summary>
/// 一套代理 + 假 CDN + 一条登记的测试夹具.
/// </summary>
internal sealed class ProxyFixture : IDisposable
{
    private ProxyFixture(MediaBufferProxy proxy, FakeCdn cdn, MediaProxyRegistration registration, string baseUrl)
    {
        Proxy = proxy;
        Cdn = cdn;
        Registration = registration;
        BaseUrl = baseUrl;
    }

    public MediaBufferProxy Proxy { get; }

    public FakeCdn Cdn { get; }

    public MediaProxyRegistration Registration { get; }

    public string BaseUrl { get; }

    public static Task<ProxyFixture> StartAsync(int payloadSize, int blockSize)
    {
        var payload = FakeCdn.CreatePayload(payloadSize);
        var cdn = new FakeCdn(payload);
        var proxy = new MediaBufferProxy(new MediaProxyOptions
        {
            BlockSize = blockSize,
            CapacityBytes = 16L * 1024 * 1024,
            SessionToken = "testtoken0123456789abcdef",
            UpstreamTimeout = TimeSpan.FromSeconds(10),
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

        var identity = MediaStreamIdentity.Create(
            "BV1fixture",
            "2002",
            "80",
            "7",
            "video/mp4",
            2_000_000,
            null,
            null,
            cdn.Url + "?deadline=1&upsig=x");
        var descriptor = new MediaStreamSourceDescriptor(cdn.Url, identity)
        {
            Cookie = "SESSDATA=fake-cookie",
            Referer = "https://www.bilibili.com",
            UserAgent = "MediaCache.Tests/1.0",
            DisplayName = "假 CDN 测试源",
            OwnerKey = "fixture",
        };
        var registration = proxy.Register(descriptor);
        var baseUrl = $"http://127.0.0.1:{proxy.Port}/";
        return Task.FromResult(new ProxyFixture(proxy, cdn, registration, baseUrl));
    }

    public void Dispose()
    {
        Registration.Dispose();
        Proxy.Dispose();
        Cdn.Dispose();
    }
}
