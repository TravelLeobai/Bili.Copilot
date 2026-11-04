// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 多线程 CDN 分块加速代理.
/// 思路取自 Bilibili-thread-ripper：把文件按块切开，用多条连接从多个 CDN 节点同时拉，
/// 播放器只跟本机这个代理说话；代理按块缓存，播放到哪块就把哪块拼出去.
/// </summary>
internal static class CdnStreamProxy
{
    private const int ChunkSize = 1024 * 1024;

    private static readonly ConcurrentDictionary<string, StreamEntry> Entries = new();
    private static readonly HttpClient Client = CreateClient();
    private static HttpListener? _listener;
    private static int _port;
    private static readonly object StartLock = new();

    /// <summary>
    /// 是否已启动.
    /// </summary>
    public static bool IsRunning => _listener?.IsListening == true;

    /// <summary>
    /// 本机代理前缀.
    /// </summary>
    public static string BaseUrl => IsRunning ? $"http://127.0.0.1:{_port}/s/" : string.Empty;

    /// <summary>
    /// 启动代理（幂等）.
    /// </summary>
    /// <returns>是否可用.</returns>
    public static bool EnsureStarted()
    {
        if (IsRunning)
        {
            return true;
        }

        lock (StartLock)
        {
            if (IsRunning)
            {
                return true;
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var port = Random.Shared.Next(18000, 24000);
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    _port = port;
                    _ = Task.Run(() => AcceptLoopAsync(listener));
                    Serilog.Log.Information("多线程加速代理已启动：127.0.0.1:{Port}", port);
                    return true;
                }
                catch (HttpListenerException)
                {
                    // 端口被占，换一个.
                }
            }

            Serilog.Log.Warning("多线程加速代理启动失败（端口都被占用）");
            return false;
        }
    }

    /// <summary>
    /// 把远端地址包成本机代理地址.
    /// </summary>
    /// <param name="remoteUrl">原始播放地址.</param>
    /// <param name="userAgent">请求用的 UA，部分 CDN 需要.</param>
    /// <returns>代理地址；不可用时返回原地址.</returns>
    public static string Wrap(string remoteUrl, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || !EnsureStarted())
        {
            return remoteUrl;
        }

        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(remoteUrl))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        if (Entries.TryGetValue(key, out var existing))
        {
            existing.UserAgent = userAgent;
        }
        else
        {
            Entries[key] = new StreamEntry(remoteUrl, userAgent);
        }

        return BaseUrl + key;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return client;
    }

    private static async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "多线程代理监听结束");
                break;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private static async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var key = context.Request.Url?.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? string.Empty;
            if (!Entries.TryGetValue(key, out var entry))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            await entry.PrepareAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "多线程代理请求失败");
            try
            {
                context.Response.Abort();
            }
            catch (Exception abortEx)
            {
                Serilog.Log.Debug(abortEx, "多线程代理中止响应失败");
            }
        }
    }

    private sealed class StreamEntry
    {
        private readonly string _remoteUrl;
        private readonly SemaphoreSlim _chunkLock = new(3);
        private Task<long>? _probeTask;
        private long _length = -1;

        public StreamEntry(string remoteUrl, string userAgent)
        {
            _remoteUrl = remoteUrl;
            UserAgent = userAgent;
        }

        public string UserAgent { get; set; }

        public async Task PrepareAsync(HttpListenerContext context)
        {
            if (_length < 0)
            {
                _probeTask ??= ProbeLengthAsync();
                _length = await _probeTask.ConfigureAwait(false);
            }

            if (_length <= 0)
            {
                context.Response.StatusCode = 502;
                context.Response.Close();
                return;
            }

            var start = 0L;
            var end = _length - 1;
            var rangeHeader = context.Request.Headers["Range"];
            if (!string.IsNullOrWhiteSpace(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = rangeHeader[6..].Split('-');
                if (spec.Length == 2)
                {
                    if (long.TryParse(spec[0], out var s))
                    {
                        start = s;
                    }

                    if (long.TryParse(spec[1], out var e))
                    {
                        end = Math.Min(e, _length - 1);
                    }
                }

                context.Response.StatusCode = 206;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{_length}";
            }
            else
            {
                context.Response.StatusCode = 200;
            }

            var count = end - start + 1;
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.Headers["Content-Type"] = "video/mp4";
            context.Response.ContentLength64 = count;

            var firstChunk = (int)(start / ChunkSize);
            var lastChunk = (int)(end / ChunkSize);
            _ = PrefetchAsync(firstChunk, Math.Min(lastChunk + 3, TotalChunks - 1));

            var buffer = new byte[64 * 1024];
            for (var index = firstChunk; index <= lastChunk; index++)
            {
                var data = await GetChunkAsync(index).ConfigureAwait(false);
                if (data is null)
                {
                    context.Response.Abort();
                    return;
                }

                var offsetInChunk = index == firstChunk ? (int)(start % ChunkSize) : 0;
                var take = Math.Min(data.Length - offsetInChunk, (int)Math.Min(count, int.MaxValue));
                var written = 0;
                while (written < take)
                {
                    var slice = Math.Min(buffer.Length, take - written);
                    Buffer.BlockCopy(data, offsetInChunk + written, buffer, 0, slice);
                    await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, slice)).ConfigureAwait(false);
                    written += slice;
                }

                count -= take;
                if (count <= 0)
                {
                    break;
                }
            }

            context.Response.OutputStream.Close();
        }

        private int TotalChunks => _length <= 0 ? 0 : (int)((_length + ChunkSize - 1) / ChunkSize);

        private readonly ConcurrentDictionary<int, Task<byte[]?>> _chunks = new();

        private Task<byte[]?> GetChunkAsync(int index)
        {
            var chunkStart = (long)index * ChunkSize;
            var chunkEnd = Math.Min(chunkStart + ChunkSize, _length) - 1;
            return _chunks.GetOrAdd(index, _ => DownloadChunkAsync(chunkStart, chunkEnd));
        }

        private async Task PrefetchAsync(int from, int to)
        {
            for (var i = from; i <= to; i++)
            {
                if (_chunks.ContainsKey(i))
                {
                    continue;
                }

                var index = i;
                var chunkStart = (long)index * ChunkSize;
                var chunkEnd = Math.Min(chunkStart + ChunkSize, _length) - 1;
                _ = _chunks.GetOrAdd(index, _ => DownloadChunkAsync(chunkStart, chunkEnd));
            }

            await Task.CompletedTask.ConfigureAwait(false);
            _ = _chunkLock;
        }

        private async Task<byte[]?> DownloadChunkAsync(long start, long end)
        {
            var hosts = CdnAccelerator.GetRankedHosts();
            if (hosts.Count == 0)
            {
                hosts = CdnPoolService.GetCandidates();
            }

            var candidates = new List<string> { _remoteUrl };
            foreach (var host in hosts.Take(3))
            {
                var swapped = CdnAccelerator.SwapHost(_remoteUrl, host);
                if (!string.IsNullOrWhiteSpace(swapped) && swapped != _remoteUrl)
                {
                    candidates.Add(swapped);
                }
            }

            foreach (var url in candidates)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(UserAgent) ? "Mozilla/5.0" : UserAgent);
                    request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
                    request.Headers.TryAddWithoutValidation("Range", $"bytes={start}-{end}");
                    using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (data.Length > 0)
                    {
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    // 这条连接不成，记一笔再换下一个候选.
                    Serilog.Log.Debug(ex, "多线程加速：分支下载失败");
                }
            }

            CdnStatusBus.Report($"分块下载失败（{start}-{end}）");
            return null;
        }

        private async Task<long> ProbeLengthAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _remoteUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(UserAgent) ? "Mozilla/5.0" : UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
                request.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (response.Content.Headers.ContentRange?.Length is { } total)
                {
                    return total;
                }

                return response.Content.Headers.ContentLength ?? -1;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "多线程加速：探测文件长度失败");
                return -1;
            }
        }
    }
}
