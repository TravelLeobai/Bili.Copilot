// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using BiliCopilot.UI.Services.Media;

namespace MediaCache.Tests;

/// <summary>
/// 代理登记 / 租约的释放路径测试：换集释放、前缀释放、空闲回收、Stop/Start。
/// 这些用例守的是 F 复审里的 blocker 1 —— 登记与租约必须有人释放，
/// 否则缓存条目会被租约永久钉住（有租约的条目不允许淘汰）。
/// </summary>
internal static class ProxyLifecycleTests
{
    private const int BlockSize = 256 * 1024;
    private const int PayloadSize = 4 * BlockSize;

    public static void Register()
    {
        TestHost.Add("代理：登记释放后缓存条目立刻可淘汰（不再被租约钉住）", () =>
        {
            // 容量只够一块：A 的租约还在时 B 写不进去；A 的登记释放后 B 立刻能写.
            using var cdn = new FakeCdn(FakeCdn.CreatePayload(2 * BlockSize));
            using var proxy = CreateProxy(capacityBytes: BlockSize);
            proxy.Start();

            var a = proxy.Register(Descriptor(cdn.Url, "player:BV1release:1", "release-a"));
            Assert.True(a.Lease.Length is null, "还没探测，长度应为 null");
            a.Lease.Length = 2 * BlockSize;
            Assert.True(a.Lease.TryWriteBlock(0, CreateBytes(BlockSize, 1)), "A 的第一块应写入成功");

            var b = proxy.Register(Descriptor(cdn.Url, "player:BV1releaseB:1", "release-b"));
            b.Lease.Length = BlockSize;
            Assert.False(b.Lease.TryWriteBlock(0, CreateBytes(BlockSize, 2)), "A 还被租着时 B 应写不进去（缓存满了）");

            proxy.ReleaseAllOwnedBy("player:BV1release:1");

            Assert.True(b.Lease.TryWriteBlock(0, CreateBytes(BlockSize, 3)), "A 释放后 B 应能写进去（条目可淘汰）");
            Assert.Equal(1, proxy.GetStatistics().ActiveRegistrations, "只剩 B 一条登记");
            b.Dispose();
        });

        TestHost.Add("代理：按归属键前缀批量释放（视频 + 音频一起走，别的视频不受影响）", async () =>
        {
            using var cdn = new FakeCdn(FakeCdn.CreatePayload(2 * BlockSize));
            using var proxy = CreateProxy();
            proxy.Start();
            using var client = CreateClient();

            var video = proxy.Register(Descriptor(cdn.Url, "player:BV1prefix:1", "prefix-video"));
            var audio = proxy.Register(Descriptor(cdn.Url, "player:BV1prefix:1", "prefix-audio"));
            var other = proxy.Register(Descriptor(cdn.Url, "player:BV1other:2", "prefix-other"));
            Assert.Equal(3, proxy.GetStatistics().ActiveRegistrations, "应有 3 条登记");

            var released = proxy.ReleaseAllOwnedByPrefix("player:BV1prefix:");
            Assert.Equal(2, released, "前缀应命中 2 条");

            var videoResponse = await client.GetAsync(video.ProxyUrl);
            Assert.Equal(HttpStatusCode.NotFound, videoResponse.StatusCode, "视频登记释放后应 404");
            var audioResponse = await client.GetAsync(audio.ProxyUrl);
            Assert.Equal(HttpStatusCode.NotFound, audioResponse.StatusCode, "音频登记释放后应 404");
            var otherResponse = await client.GetAsync(other.ProxyUrl);
            Assert.True(
                otherResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent,
                "别的视频的登记不该被前缀释放，实际 " + (int)otherResponse.StatusCode);

            other.Dispose();
        });

        TestHost.Add("代理：同归属键重复包装时只保留指定登记（旧登记立刻失效）", async () =>
        {
            using var cdn = new FakeCdn(FakeCdn.CreatePayload(2 * BlockSize));
            using var proxy = CreateProxy();
            proxy.Start();
            using var client = CreateClient();

            var old = proxy.Register(Descriptor(cdn.Url, "player:BV1rewrap:1", "rewrap-old"));
            var current = proxy.Register(Descriptor(cdn.Url, "player:BV1rewrap:1", "rewrap-current"));

            var released = proxy.ReleaseAllOwnedBy("player:BV1rewrap:1", new[] { current.Id });
            Assert.Equal(1, released, "只应释放旧的那条");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(old.ProxyUrl)).StatusCode, "旧登记应 404");
            Assert.True(
                (await client.GetAsync(current.ProxyUrl)).StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent,
                "保留的登记应照常可用");
            current.Dispose();
        });

        TestHost.Add("代理：默认空闲回收是 3 分钟（不是 45 分钟）", () =>
        {
            var options = new MediaProxyOptions();
            Assert.Equal(TimeSpan.FromMinutes(3), options.RegistrationIdleTimeout, "默认空闲回收时长");
            Assert.True(options.JanitorInterval <= TimeSpan.FromMinutes(1), "回收扫描间隔应 <= 1 分钟");
        });

        TestHost.Add("代理：空闲登记超过回收时长后被释放（可注入时钟，不 sleep）", async () =>
        {
            var clock = new ManualTimeProvider();
            using var cdn = new FakeCdn(FakeCdn.CreatePayload(2 * BlockSize));
            using var proxy = CreateProxy(
                janitorInterval: TimeSpan.FromMilliseconds(50),
                idleTimeout: TimeSpan.FromMinutes(3),
                timeProvider: clock);
            proxy.Start();
            using var client = CreateClient();

            var registration = proxy.Register(Descriptor(cdn.Url, "player:BV1idle:1", "idle"));
            Assert.Equal(1, proxy.GetStatistics().ActiveRegistrations, "刚登记应还在");

            // 推进 2 分钟：还没到 3 分钟，不该回收.
            clock.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(300);
            Assert.Equal(1, proxy.GetStatistics().ActiveRegistrations, "未到回收时长不应释放");

            // 再推进 2 分钟（累计 4 分钟 > 3 分钟）：下一次扫描应释放.
            clock.Advance(TimeSpan.FromMinutes(2));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (proxy.GetStatistics().ActiveRegistrations > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Equal(0, proxy.GetStatistics().ActiveRegistrations, "空闲超时后登记应被回收");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(registration.ProxyUrl)).StatusCode, "回收后地址应 404");
        });

        TestHost.Add("代理：Stop 释放登记 + 清空缓存 + 关端口，再次 Start 能起来", async () =>
        {
            using var cdn = new FakeCdn(FakeCdn.CreatePayload(PayloadSize));
            using var proxy = CreateProxy();
            proxy.Start();
            using var client = CreateClient();
            var registration = proxy.Register(Descriptor(cdn.Url, "player:BV1stop:1", "stop"));
            var port = proxy.Port;

            var (warm, _) = await GetRangeAsync(client, registration.ProxyUrl, 0, 999);
            Assert.Equal(HttpStatusCode.PartialContent, warm.StatusCode, "预热状态码");
            Assert.True(proxy.Cache.GetStatistics().CachedBlockCount > 0, "预热后应有缓存块");

            proxy.Stop();

            Assert.False(proxy.IsRunning, "停止后不应在监听");
            Assert.Equal(0, proxy.GetStatistics().ActiveRegistrations, "登记应全部释放");
            Assert.Equal(0L, proxy.GetStatistics().Cache.CurrentBytes, "缓存应清空");
            // 监听已经停掉：旧端口连不上（不是 404 —— 根本没有服务在听）.
            Assert.False(await CanConnectAsync(port), "停止后端口应已关闭");

            // 同一个实例允许再次启动：客户端要能在新端口上正常取数据.
            proxy.Start();
            Assert.True(proxy.IsRunning, "再次 Start 后应在监听");
            Assert.True(proxy.Port > 0, "应有新端口");

            var again = proxy.Register(Descriptor(cdn.Url, "player:BV1restart:2", "restart"));
            var (response, body) = await GetRangeAsync(client, again.ProxyUrl, 0, 999);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode, "重启后状态码");
            Assert.True(body.AsSpan().SequenceEqual(cdn.Payload.AsSpan(0, 1000)), "重启后字节应与源一致");
            again.Dispose();
        });

        TestHost.Add("代理：Dispose 后不再接受 Start，且端口已关", async () =>
        {
            using var cdn = new FakeCdn(FakeCdn.CreatePayload(BlockSize));
            var proxy = CreateProxy();
            proxy.Start();
            var port = proxy.Port;
            proxy.Dispose();

            Assert.False(await CanConnectAsync(port), "Dispose 后端口应关闭");
            var threw = false;
            try
            {
                proxy.Start();
            }
            catch (ObjectDisposedException)
            {
                threw = true;
            }

            Assert.True(threw, "Dispose 之后再 Start 应抛 ObjectDisposedException");
        });
    }

    internal static MediaBufferProxy CreateProxy(
        long capacityBytes = 16L * 1024 * 1024,
        TimeSpan? janitorInterval = null,
        TimeSpan? idleTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var options = new MediaProxyOptions
        {
            BlockSize = BlockSize,
            CapacityBytes = capacityBytes,
            SessionToken = "lifecycletoken0123456789ab",
            UpstreamTimeout = TimeSpan.FromSeconds(10),
            MaxUpstreamRetries = 0,
            TimeProvider = timeProvider ?? TimeProvider.System,
        };
        if (janitorInterval is { } interval)
        {
            options.JanitorInterval = interval;
        }

        if (idleTimeout is { } idle)
        {
            options.RegistrationIdleTimeout = idle;
        }

        return new MediaBufferProxy(options)
        {
            Log = (level, message) =>
            {
                if (level >= MediaProxyLogLevel.Warning)
                {
                    Console.WriteLine($"        [proxy:{level}] {message}");
                }
            },
        };
    }

    internal static MediaStreamSourceDescriptor Descriptor(string url, string ownerKey, string tag)
        => new(url, Identity(tag))
        {
            Cookie = "SESSDATA=fake",
            Referer = "https://www.bilibili.com",
            UserAgent = "MediaCache.Tests/1.0",
            DisplayName = tag,
            OwnerKey = ownerKey,
        };

    internal static MediaStreamIdentity Identity(string tag)
        => MediaStreamIdentity.Create(
            "BV1lifecycle",
            "3003",
            tag,
            "7",
            "video/mp4",
            2_000_000,
            null,
            null,
            $"https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/cc/dd/{tag}.m4s?deadline=1&upsig=x");

    internal static HttpClient CreateClient()
        => new(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };

    internal static async Task<(HttpResponseMessage Response, byte[] Body)> GetRangeAsync(HttpClient client, string url, long start, long end)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return (response, await response.Content.ReadAsByteArrayAsync());
    }

    internal static byte[] CreateBytes(int length, int seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 31) + seed);
        }

        return data;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 可控时钟：测试里推进它来触发「空闲回收」，不用真的 sleep 3 分钟.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override string ToString()
        => _now.ToString("O", CultureInfo.InvariantCulture);
}
