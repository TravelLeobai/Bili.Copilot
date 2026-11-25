// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using BiliCopilot.UI.Services.Media;

namespace MediaCache.Tests;

/// <summary>
/// 取消相关的测试（F 复审的 blocker 3）：
/// 请求方取消后，代理在飞的块下载必须跟着断、半截块不许写进缓存，
/// 后续同一 range 的请求重新拉一遍（只多拉一次，不产生重复 flight）；
/// 预取里 Register 之后的异常路径必须把登记与租约还回去。
/// </summary>
internal static class MediaCancelTests
{
    private const int BlockSize = 256 * 1024;
    private const string BlockZeroRange = "bytes=0-262143";

    public static void Register()
    {
        TestHost.Add("代理：读请求中途取消 → 不写缓存、上游连接被断、后续重新拉且只多一次", async () =>
        {
            using var cdn = new RawRangeCdn(FakeCdn.CreatePayload(4 * BlockSize));
            using var proxy = ProxyLifecycleTests.CreateProxy();
            proxy.Start();
            using var client = ProxyLifecycleTests.CreateClient();
            using var cts = new CancellationTokenSource();

            var registration = proxy.Register(ProxyLifecycleTests.Descriptor(cdn.Url, "player:BV1cancel:7", "cancel-read"));

            using var request = new HttpRequestMessage(HttpMethod.Get, registration.ProxyUrl);
            request.Headers.Range = new RangeHeaderValue(0, BlockSize - 1);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode, "首个响应状态码");

            // 假 CDN 已经把「半截块」写出来了：说明代理的块下载正在飞.
            await cdn.PartialBodySent.WaitAsync(TimeSpan.FromSeconds(10));

            // 请求方取消（等价于 mpv seek / 关掉这条连接）.
            cts.Cancel();
            response.Dispose();

            // ① 上游连接被断：假 CDN 在挂住的地方读到了 FIN.
            await cdn.WaitForDisconnectAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, cdn.DisconnectedConnections, "请求方取消后上游连接应被断开");

            // ② 缓存里没有这个块，也没被标成完整.
            Assert.Equal(0, proxy.Cache.GetStatistics().CachedBlockCount, "取消后缓存里不应有任何块");
            Assert.False(registration.Lease.ContainsBlock(0), "第 0 块不应被标记为完整");

            // ③ 后续完整请求能补齐，而且上游只多拉一次（不产生重复 flight）.
            var (second, bytes) = await ProxyLifecycleTests.GetRangeAsync(client, registration.ProxyUrl, 0, BlockSize - 1);
            Assert.Equal(HttpStatusCode.PartialContent, second.StatusCode, "补齐请求状态码");
            Assert.Equal(BlockSize, bytes.Length, "补齐请求长度");
            Assert.True(bytes.AsSpan().SequenceEqual(cdn.Payload.AsSpan(0, BlockSize)), "补齐内容应与源一致");
            Assert.True(registration.Lease.ContainsBlock(0), "补齐后第 0 块应在缓存里");
            Assert.Equal(
                2,
                cdn.CountRanges(BlockZeroRange),
                "同一块只该多拉一次，实际上游记录 [" + string.Join(" | ", cdn.RangeHeaders) + "]");
        });

        TestHost.Add("预取：中途取消不写缓存，后续完整预取补齐且只多拉一次", async () =>
        {
            using var cdn = new RawRangeCdn(FakeCdn.CreatePayload(4 * BlockSize));
            using var proxy = ProxyLifecycleTests.CreateProxy();
            proxy.Start();
            using var prefetch = new MediaPrefetchService(proxy, new MediaPrefetchOptions
            {
                MaxConcurrent = 1,
                BudgetBytes = 32L * 1024 * 1024,
                RequestTimeout = TimeSpan.FromSeconds(10),
            });
            var target = prefetch.RegisterTarget(
                ProxyLifecycleTests.Descriptor(cdn.Url, "page-BV1prefetchcancel", "prefetch-cancel"),
                new MediaPrefetchSourceInfo { DisplayName = "取消预取", BandwidthBps = 2_000_000 });

            using var cts = new CancellationTokenSource();
            var task = prefetch.PrefetchSecondsAsync(target, 3, cts.Token);
            await cdn.PartialBodySent.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            var outcome = await task;

            Assert.True(outcome.Cancelled, "应返回取消结果，实际：" + outcome.Note);
            await cdn.WaitForDisconnectAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, cdn.DisconnectedConnections, "取消后被挂住的上游连接应被断开");
            Assert.Equal(0, proxy.Cache.GetStatistics().CachedBlockCount, "取消后缓存里不应有块");
            Assert.False(target.Registration.Lease.ContainsBlock(0), "第 0 块不应被标记为完整");

            var retry = await prefetch.PrefetchSecondsAsync(target, 3, CancellationToken.None);
            Assert.True(retry.Success, "后续完整预取应成功：" + retry.Note);
            Assert.True(target.Registration.Lease.ContainsBlock(0), "补齐后第 0 块应在缓存里");
            Assert.Equal(
                2,
                cdn.CountRanges(BlockZeroRange),
                "同一块只该多拉一次，实际上游记录 [" + string.Join(" | ", cdn.RangeHeaders) + "]");
        });

        TestHost.Add("预取：登记成功后挂目标失败 → 登记与租约都被释放（不会钉住缓存）", () =>
        {
            using var cdn = new FakeCdn(FakeCdn.CreatePayload(BlockSize));
            using var proxy = ProxyLifecycleTests.CreateProxy();
            proxy.Start();
            using var prefetch = new MediaPrefetchService(proxy, new MediaPrefetchOptions
            {
                BudgetBytes = 16L * 1024 * 1024,
            });
            prefetch.AfterRegistrationHook = _ => throw new InvalidOperationException("注入的失败：挂目标之前炸");

            var threw = false;
            try
            {
                prefetch.RegisterTarget(
                    ProxyLifecycleTests.Descriptor(cdn.Url, "page-BV1attachfail", "attach-fail"),
                    new MediaPrefetchSourceInfo { DisplayName = "挂目标失败", BandwidthBps = 2_000_000 });
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw, "注入的异常应该原样抛出来");
            Assert.Equal(0, proxy.GetStatistics().ActiveRegistrations, "登记必须被释放");
            Assert.Equal(0, proxy.GetStatistics().Cache.EntryCount, "租约还回去后空条目也应该被收掉");
            Assert.Equal(0, prefetch.Targets.Count, "失败的目标不应留在目标表里");
        });
    }
}

/// <summary>
/// 极简「裸 TCP」Range CDN：和 FakeCdn 不同，它能直接看出上游连接什么时候被断 ——
/// 发完半截响应体后挂住，同时读这条连接：读到 0 字节就是客户端（代理）把连接关了。
/// 用它来断言「请求方取消 → 上游连接被断」这条要求。
/// </summary>
internal sealed class RawRangeCdn : IDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _payload;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _ranges = [];
    private readonly object _gate = new();
    private readonly TaskCompletionSource _partialBodySent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseRest = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _requestCount;
    private int _disconnects;
    private int _holdRemaining;
    private int _disposed;

    public RawRangeCdn(byte[] payload, bool holdFirstMediaBody = true)
    {
        _payload = payload;
        _holdRemaining = holdFirstMediaBody ? 1 : 0;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(16);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/media/raw.m4s");
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url { get; }

    public byte[] Payload => _payload;

    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// 被对端关掉的连接数（挂住时读到 0 字节，或写出时 IOException）.
    /// </summary>
    public int DisconnectedConnections => Volatile.Read(ref _disconnects);

    /// <summary>
    /// 第一条媒体块请求已经发出半截响应体（此刻代理的上游下载正在飞）.
    /// </summary>
    public Task PartialBodySent => _partialBodySent.Task;

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

    public int CountRanges(string range)
    {
        lock (_gate)
        {
            return _ranges.Count(p => string.Equals(p, range, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 放行被挂住的连接（测试收尾用）.
    /// </summary>
    public void ReleaseRest() => _releaseRest.TrySetResult();

    public async Task WaitForDisconnectAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DisconnectedConnections == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        ReleaseRest();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var rangeText = await ReadRangeHeaderAsync(stream);
                if (rangeText is null)
                {
                    return;
                }

                Interlocked.Increment(ref _requestCount);
                lock (_gate)
                {
                    _ranges.Add(rangeText);
                }

                var total = _payload.Length;
                var probe = string.Equals(rangeText, "bytes=0-0", StringComparison.Ordinal);
                long start;
                long end;
                if (probe)
                {
                    start = 0;
                    end = 0;
                }
                else if (!TryParseRange(rangeText, total, out start, out end))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{total}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    await stream.FlushAsync();
                    return;
                }

                var length = end - start + 1;
                var head = string.Create(
                    CultureInfo.InvariantCulture,
                    $"HTTP/1.1 206 Partial Content\r\nContent-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Range: bytes {start}-{end}/{total}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));

                var body = _payload.AsMemory((int)start, (int)length);
                var hold = !probe && Interlocked.Exchange(ref _holdRemaining, 0) == 1;
                if (hold)
                {
                    // 先发一半，然后等两件事之一：对端关连接（读到 0）或测试放行.
                    var half = Math.Max(1, (int)(length / 2));
                    await stream.WriteAsync(body[..half]);
                    await stream.FlushAsync();
                    _partialBodySent.TrySetResult();

                    if (await WaitForClientCloseAsync(stream))
                    {
                        Interlocked.Increment(ref _disconnects);
                    }

                    return;
                }

                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch (IOException)
            {
                Interlocked.Increment(ref _disconnects);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// 读这条连接：读到 0 字节 = 对端关了（返回 true）；测试放行（返回 false）.
    /// </summary>
    private async Task<bool> WaitForClientCloseAsync(NetworkStream stream)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var buffer = new byte[1];
        var read = stream.ReadAsync(buffer, linked.Token).AsTask();
        var winner = await Task.WhenAny(read, _releaseRest.Task);
        if (winner != read)
        {
            // 测试放行：这条连接不再需要，收尾时它会随着 socket 关闭.
            linked.Cancel();
            try
            {
                await read;
            }
            catch
            {
            }

            return false;
        }

        try
        {
            return await read == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// 读请求头，返回 Range 头（没有 Range 记 "none"）；连接直接断掉时返回 null.
    /// </summary>
    private static async Task<string?> ReadRangeHeaderAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var accumulated = new List<byte>();
        while (accumulated.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                return null;
            }

            for (var i = 0; i < read; i++)
            {
                accumulated.Add(buffer[i]);
            }

            var text = Encoding.ASCII.GetString(accumulated.ToArray());
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            foreach (var line in text[..end].Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line[..colon].Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colon + 1)..].Trim();
                }
            }

            return "none";
        }

        return "none";
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
        if (dash <= 0)
        {
            return false;
        }

        if (!long.TryParse(spec[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start))
        {
            return false;
        }

        var endText = spec[(dash + 1)..];
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
