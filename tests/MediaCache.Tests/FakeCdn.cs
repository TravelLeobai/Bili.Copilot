// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MediaCache.Tests;

/// <summary>
/// 本地假 CDN：127.0.0.1 上另一个端口，返回固定字节数组，支持 Range，并记录每次请求的 Range 头.
/// 用来断言「代理到底有没有再打上游」.
/// </summary>
internal sealed class FakeCdn : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _payload;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _ranges = [];
    private readonly object _gate = new();
    private int _requestCount;
    private int _failRemaining;
    private int _disposed;

    public FakeCdn(byte[] payload, string path = "/media/test.m4s")
    {
        _payload = payload;
        Port = GetFreePort();
        _listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/"));
        _listener.Start();
        Url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}{path}");
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public string Url { get; }

    public byte[] Payload => _payload;

    /// <summary>
    /// 收到过多少个请求（含探测与重试）.
    /// </summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// 每个请求的 Range 头（无 Range 记为 "none"）.
    /// </summary>
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

    /// <summary>
    /// 人为延迟响应，用来制造并发重叠.
    /// </summary>
    public int ResponseDelayMilliseconds { get; set; }

    /// <summary>
    /// 接下来的 N 个请求返回 403（测退避重试）.
    /// </summary>
    /// <param name="count">次数.</param>
    public void FailNextRequests(int count) => Volatile.Write(ref _failRemaining, count);

    public void ClearRecorded()
    {
        lock (_gate)
        {
            _ranges.Clear();
        }
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
            var rangeHeader = context.Request.Headers["Range"];
            lock (_gate)
            {
                _ranges.Add(string.IsNullOrEmpty(rangeHeader) ? "none" : rangeHeader);
            }

            if (ResponseDelayMilliseconds > 0)
            {
                await Task.Delay(ResponseDelayMilliseconds);
            }

            if (Interlocked.Decrement(ref _failRemaining) >= 0)
            {
                context.Response.StatusCode = 403;
                context.Response.ContentLength64 = 0;
                context.Response.Close();
                return;
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

    /// <summary>
    /// 造一份可复现的假媒体字节.
    /// </summary>
    /// <param name="length">长度.</param>
    /// <param name="seed">种子.</param>
    /// <returns>字节.</returns>
    public static byte[] CreatePayload(int length, int seed = 20261125)
    {
        var random = new Random(seed);
        var data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    public static string Describe(byte[] data)
    {
        var builder = new StringBuilder();
        builder.Append("len=").Append(data.Length);
        return builder.ToString();
    }
}
