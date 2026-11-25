// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 代理日志级别（这个文件不依赖 Microsoft.Extensions.Logging，方便单独单测；
/// 应用侧用 <c>MediaBufferProxyBridge</c> 把 ILogger 适配进来）.
/// </summary>
public enum MediaProxyLogLevel
{
    /// <summary>调试.</summary>
    Debug,

    /// <summary>信息.</summary>
    Information,

    /// <summary>警告.</summary>
    Warning,

    /// <summary>错误.</summary>
    Error,
}

/// <summary>
/// 代理日志回调.
/// </summary>
/// <param name="level">级别.</param>
/// <param name="message">文本（调用方保证其中不含 Cookie / token / 完整签名 URL）.</param>
public delegate void MediaProxyLogHandler(MediaProxyLogLevel level, string message);

/// <summary>
/// 登记给代理的一条媒体源：播放地址 + 鉴权头 + 逻辑身份.
/// <para>
/// 代理只按内部 id 取源，绝不接受外部传进来的 URL —— 这是防 SSRF / 本地文件读取的关键，
/// 所以 URL 只能通过这个描述对象登记进来.
/// </para>
/// </summary>
public sealed class MediaStreamSourceDescriptor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaStreamSourceDescriptor"/> class.
    /// </summary>
    /// <param name="url">真实播放地址（带 CDN 签名，只在本进程内使用）.</param>
    /// <param name="identity">逻辑媒体身份，决定缓存 key.</param>
    public MediaStreamSourceDescriptor(string url, MediaStreamIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(identity);
        Url = url;
        Identity = identity;
    }

    /// <summary>
    /// 真实播放地址（绝不出现在日志里）.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// 逻辑媒体身份.
    /// </summary>
    public MediaStreamIdentity Identity { get; }

    /// <summary>
    /// 上游 Cookie（B 站 CDN 需要）. 仅本进程内使用.
    /// </summary>
    public string? Cookie { get; set; }

    /// <summary>
    /// 上游 Referer.
    /// </summary>
    public string? Referer { get; set; }

    /// <summary>
    /// 上游 User-Agent.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// 显示名（日志用，建议写成「标题 + 分P + 画质」，不要放 URL）.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 归属键：同一个键的登记会被一起释放（例如同一个视频的不同分 P）.
    /// 换集 / 换视频时用它做批量清理，避免缓存条目一直被租约钉住.
    /// </summary>
    public string OwnerKey { get; set; } = string.Empty;
}

/// <summary>
/// 代理可调参数.
/// </summary>
public sealed class MediaProxyOptions
{
    /// <summary>
    /// 缓存容量上限（字节），默认 256 MiB.
    /// </summary>
    public long CapacityBytes { get; set; } = MediaRangeCache.DefaultCapacityBytes;

    /// <summary>
    /// 块大小（字节），默认 256 KiB.
    /// </summary>
    public int BlockSize { get; set; } = MediaRangeCache.DefaultBlockSize;

    /// <summary>
    /// 会话 token；为 null 时自动生成. 测试里可以传固定值.
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// 单次上游请求超时.
    /// </summary>
    public TimeSpan UpstreamTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 读请求头的超时（防慢速连接占着不放）.
    /// </summary>
    public TimeSpan RequestHeaderTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 登记空闲多久后自动释放（默认 3 分钟）.
    /// <para>
    /// 登记持有缓存租约，而带租约的条目不允许被淘汰；空闲回收窗口过长（原本是 45 分钟）会让
    /// 连播多个视频后缓存写不进去、内存一直顶在容量上限。现在降到 3 分钟：换集 / 关播放器时
    /// 还会主动释放，这是最后的兜底。
    /// </para>
    /// </summary>
    public TimeSpan RegistrationIdleTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 空闲登记回收的扫描间隔（默认 1 分钟；测试里可以调小）.
    /// </summary>
    public TimeSpan JanitorInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 时钟（测试里可以注入可控时钟来验证空闲回收，生产环境保持 <see cref="TimeProvider.System"/>）.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// 上游 403 / 412 / 429 的最大重试次数（指数退避），默认 2.
    /// </summary>
    public int MaxUpstreamRetries { get; set; } = 2;
}

/// <summary>
/// 一条登记：把内部 id 映射到一个媒体源，并持有缓存租约.
/// 用 <see cref="Dispose"/>（或 <see cref="MediaBufferProxy.Release"/>）结束它.
/// </summary>
public sealed class MediaProxyRegistration : IDisposable
{
    private readonly MediaBufferProxy _proxy;
    private int _disposed;
    private long _lastAccessTicks;

    internal MediaProxyRegistration(MediaBufferProxy proxy, string id, string proxyUrl, MediaStreamSourceDescriptor source, MediaCacheLease lease)
    {
        _proxy = proxy;
        Id = id;
        ProxyUrl = proxyUrl;
        Source = source;
        Lease = lease;
        _lastAccessTicks = proxy.NowTicks;
    }

    /// <summary>
    /// 内部 id（密码学随机，32 位十六进制）.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 给播放器用的代理地址：<c>http://127.0.0.1:{port}/m/{token}/{id}</c>.
    /// </summary>
    public string ProxyUrl { get; }

    /// <summary>
    /// 登记的源.
    /// </summary>
    public MediaStreamSourceDescriptor Source { get; }

    /// <summary>
    /// 媒体身份.
    /// </summary>
    public MediaStreamIdentity Identity => Source.Identity;

    /// <summary>
    /// 上游是否支持 Range（探测后才有意义）.
    /// </summary>
    internal bool SupportsRange { get; set; } = true;

    /// <summary>
    /// 上游资源总长度；null 表示还没探测到.
    /// </summary>
    internal long? Length => Lease.Length;

    /// <summary>
    /// 长度探测任务（同一登记只探测一次）.
    /// </summary>
    internal Task<bool>? ProbeTask { get; set; }

    /// <summary>
    /// 探测任务的锁.
    /// </summary>
    internal object ProbeGate { get; } = new();

    internal MediaCacheLease Lease { get; }

    internal long LastAccessTicks => Volatile.Read(ref _lastAccessTicks);

    internal void Touch() => Volatile.Write(ref _lastAccessTicks, _proxy.NowTicks);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _proxy.Release(Id);
    }
}

/// <summary>
/// 代理统计快照.
/// </summary>
/// <param name="Port">监听端口.</param>
/// <param name="ActiveRegistrations">当前登记数.</param>
/// <param name="Requests">收到的请求数.</param>
/// <param name="RangeRequests">返回过 206 的请求数.</param>
/// <param name="UpstreamRequests">打到上游的请求数（含探测与重试）.</param>
/// <param name="UpstreamRetries">上游重试次数.</param>
/// <param name="UpstreamBytes">从上游拉取的字节数.</param>
/// <param name="Cache">缓存统计.</param>
public readonly record struct MediaProxyStatistics(
    int Port,
    int ActiveRegistrations,
    long Requests,
    long RangeRequests,
    long UpstreamRequests,
    long UpstreamRetries,
    long UpstreamBytes,
    MediaCacheStatistics Cache)
{
    /// <inheritdoc/>
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"port={Port} regs={ActiveRegistrations} reqs={Requests} 206={RangeRequests} upstream={UpstreamRequests} retries={UpstreamRetries} upBytes={UpstreamBytes / 1024}KiB | {Cache}");
}

/// <summary>
/// 仅回环的媒体字节范围代理（HTTP/1.1，GET / HEAD + Range）.
/// <para>
/// 为什么是它：mpv 那边没有可用的自定义流回调（<c>mpv_stream_cb_add_ro</c> 在
/// Richasy.MpvKernel 里没有绑定，且禁止改原生封装），所以只能让 mpv 从本机 HTTP 取流，
/// 代理在中间做「缓存 + 合并并发请求 + 补缺失块」.
/// </para>
/// <para>
/// 安全边界：
/// <list type="bullet">
/// <item>只绑 <see cref="IPAddress.Loopback"/>，端口由系统分配（0）.</item>
/// <item>路径固定为 <c>/m/{会话token}/{登记id}</c>；token 不对、id 不对一律 404.</item>
/// <item>不接受任何 URL 参数，也不做通用转发 —— 只按内部登记的源头取，杜绝 SSRF 与本地文件读取.</item>
/// <item>日志不打印 Cookie / token / 完整签名 URL，只打 host + path 前若干字符或登记 id.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MediaBufferProxy : IDisposable
{
    /// <summary>
    /// 路径前缀.
    /// </summary>
    public const string PathRoot = "/m/";

    private const int MaxHeaderBytes = 32 * 1024;

    private readonly MediaProxyOptions _options;
    private readonly MediaRangeCache _cache;
    private readonly ConcurrentDictionary<string, MediaProxyRegistration> _registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BlockFlight> _blockFlights = new(StringComparer.Ordinal);
    private readonly HttpClient _upstream;
    private readonly TimeProvider _time;

    // 监听与取消源都在 Stop() 里按「先停监听、等后台循环退出、再释放 CTS」的顺序处理，
    // 分析器看不到这条间接路径，所以这里显式说明（否则 CA2213 会当成泄漏）.
#pragma warning disable CA2213 // Disposable fields should be disposed
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
#pragma warning restore CA2213
    private Task? _acceptTask;
    private Task? _janitorTask;
    private string _sessionToken = string.Empty;
    private int _started;
    private int _disposed;
    private long _requestCount;
    private long _rangeRequestCount;
    private long _upstreamRequests;
    private long _upstreamRetries;
    private long _upstreamBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaBufferProxy"/> class.
    /// </summary>
    /// <param name="options">可调参数.</param>
    public MediaBufferProxy(MediaProxyOptions? options = null)
    {
        _options = options ?? new MediaProxyOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
        _cache = new MediaRangeCache(_options.CapacityBytes, _options.BlockSize);
        var handler = new SocketsHttpHandler
        {
            // 媒体流绝不能被解压：一旦传输层解压，字节偏移就和原始文件对不上了.
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
        };
        _upstream = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// 日志回调；为 null 时不记日志.
    /// </summary>
    public MediaProxyLogHandler? Log { get; set; }

    /// <summary>
    /// 监听端口（未启动时为 0）.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// 缓存.
    /// </summary>
    public MediaRangeCache Cache => _cache;

    /// <summary>
    /// 是否在监听.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _started) != 0 && _acceptTask is { IsCompleted: false };

    /// <summary>
    /// 会话 token（只在进程内用，给 <see cref="MediaProxyRegistration.ProxyUrl"/> 拼路径）.
    /// </summary>
    public string SessionToken => _sessionToken;

    /// <summary>
    /// 当前时钟的 UTC ticks（空闲回收用；测试可以注入可控时钟）.
    /// </summary>
    internal long NowTicks => _time.GetUtcNow().UtcTicks;

    /// <summary>
    /// 代理自身的取消令牌；停止后给出一个已取消的令牌（任何残留循环立即退出）.
    /// </summary>
    private CancellationToken ProxyToken => _cts?.Token ?? new CancellationToken(canceled: true);

    /// <summary>
    /// 判断一个地址是不是本代理给出的地址（避免重复包装）.
    /// </summary>
    /// <param name="url">播放地址.</param>
    /// <returns>是否本代理地址.</returns>
    public static bool IsProxyUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback
            && (string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            && uri.AbsolutePath.StartsWith(PathRoot, StringComparison.Ordinal);
    }

    /// <summary>
    /// 启动监听（只绑回环，端口由系统分配）.
    /// </summary>
    /// <exception cref="InvalidOperationException">重复调用或绑定失败.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(16);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _sessionToken = _options.SessionToken ?? CreateToken();
            _listener = listener;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, token));
            _janitorTask = Task.Run(() => JanitorLoopAsync(token));
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            LogMessage(MediaProxyLogLevel.Information, string.Create(CultureInfo.InvariantCulture, $"媒体代理已启动：127.0.0.1:{Port}（块 {_cache.BlockSize / 1024} KiB，上限 {_cache.CapacityBytes / 1024 / 1024} MiB）"));
        }
        catch (Exception)
        {
            Stop();
            throw;
        }
    }

    /// <summary>
    /// 停止监听：释放全部登记（连带缓存租约）、断开在飞的上游下载、清空缓存、等后台循环退出.
    /// 幂等，且停止后可以再次 <see cref="Start"/>（应用退出时由 <see cref="Dispose"/> 调用）.
    /// </summary>
    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        var listener = Interlocked.Exchange(ref _listener, null);
        if (cts is null && listener is null && Volatile.Read(ref _started) == 0)
        {
            return;
        }

        Interlocked.Exchange(ref _started, 0);
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener?.Stop();
            listener?.Dispose();
        }
        catch
        {
        }

        // 等后台循环真的退出：留着它们就是在退出阶段留后台线程.
        WaitQuietly(Interlocked.Exchange(ref _acceptTask, null));
        WaitQuietly(Interlocked.Exchange(ref _janitorTask, null));

        // 在飞的块下载一律断掉（取消后不写缓存），再释放登记与缓存.
        foreach (var flight in _blockFlights.Values)
        {
            flight.Cancel();
        }

        foreach (var id in _registrations.Keys.ToArray())
        {
            Release(id);
        }

        _blockFlights.Clear();

        // 登记全释放后缓存里已经没有租约保护的块：整表清掉，别让下一次播放接着背这份内存.
        _cache.Clear();

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Port = 0;
        try
        {
            cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        LogMessage(MediaProxyLogLevel.Information, "媒体代理已停止");
    }

    private static void WaitQuietly(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 后台循环的异常不影响停止流程.
        }
    }

    /// <summary>
    /// 登记一个媒体源，拿到播放器可用的代理地址.
    /// </summary>
    /// <param name="descriptor">源描述.</param>
    /// <returns>登记（Dispose 即释放）.</returns>
    public MediaProxyRegistration Register(MediaStreamSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!IsRunning)
        {
            throw new InvalidOperationException("媒体代理还没启动.");
        }

        // 同一次播放里 GetSourceAsync 可能被调两次（mpv 与网页回落各一次）：
        // 归属键 + 身份 + URL 都一样时复用原登记，避免给同一份缓存叠多个租约.
        foreach (var existing in _registrations.Values)
        {
            if (!string.IsNullOrEmpty(descriptor.OwnerKey)
                && string.Equals(existing.Source.OwnerKey, descriptor.OwnerKey, StringComparison.Ordinal)
                && string.Equals(existing.Source.Url, descriptor.Url, StringComparison.Ordinal)
                && existing.Identity.Equals(descriptor.Identity))
            {
                existing.Touch();
                return existing;
            }
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        // 路径尾巴带个假扩展名：mpv 会按扩展名猜容器，没有扩展名时它只能靠内容探测，
        // 在「没有 moov 的 DASH 分片」上容易判错；扩展名只是给播放器的提示，代理不读它.
        var url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}{PathRoot}{_sessionToken}/{id}{GetUrlExtension(descriptor.Identity)}");
        var lease = _cache.AcquireLease(descriptor.Identity);
        var committed = false;
        try
        {
            var registration = new MediaProxyRegistration(this, id, url, descriptor, lease);
            _registrations[id] = registration;
            committed = true;
            LogMessage(MediaProxyLogLevel.Debug, $"登记媒体源 {id}：{Describe(descriptor)}");
            return registration;
        }
        finally
        {
            // 登记没建成时租约必须还回去：漏一个租约就会把整个缓存条目永久钉住（不可淘汰）.
            if (!committed)
            {
                lease.Dispose();
            }
        }
    }

    /// <summary>
    /// 释放一条登记.
    /// </summary>
    /// <param name="id">登记 id.</param>
    public void Release(string id)
    {
        if (string.IsNullOrEmpty(id) || !_registrations.TryRemove(id, out var registration))
        {
            return;
        }

        registration.Lease.Dispose();
        LogMessage(MediaProxyLogLevel.Debug, $"释放媒体源 {id}");
    }

    /// <summary>
    /// 释放某个归属键下的全部登记（换集 / 换视频时用）.
    /// </summary>
    /// <param name="ownerKey">归属键.</param>
    /// <returns>释放条数.</returns>
    public int ReleaseAllOwnedBy(string ownerKey) => ReleaseAllOwnedBy(ownerKey, null);

    /// <summary>
    /// 释放某个归属键下的登记，可以排除若干「刚登记、正在用」的 id.
    /// <para>
    /// 同一个归属键重复包装（换画质 / 换播放地址）时，旧的那些登记已经没有播放器在用，
    /// 用排除表留住新登记、把旧的立刻放掉，避免同一集攒下多份租约.
    /// </para>
    /// </summary>
    /// <param name="ownerKey">归属键.</param>
    /// <param name="exceptIds">要保留的登记 id；为 null 表示不排除.</param>
    /// <returns>释放条数.</returns>
    public int ReleaseAllOwnedBy(string ownerKey, IReadOnlyCollection<string>? exceptIds)
    {
        if (string.IsNullOrEmpty(ownerKey))
        {
            return 0;
        }

        var keep = exceptIds is { Count: > 0 } ? new HashSet<string>(exceptIds, StringComparer.Ordinal) : null;
        var count = 0;
        foreach (var pair in _registrations)
        {
            if (!string.Equals(pair.Value.Source.OwnerKey, ownerKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (keep is not null && keep.Contains(pair.Key))
            {
                continue;
            }

            Release(pair.Key);
            count++;
        }

        if (count > 0)
        {
            LogMessage(MediaProxyLogLevel.Debug, $"按归属键释放 {count} 条登记");
        }

        return count;
    }

    /// <summary>
    /// 释放归属键以 <paramref name="prefix"/> 开头的全部登记（拿不到 cid 时按 bvid 前缀释放）.
    /// </summary>
    /// <param name="prefix">归属键前缀，例如 <c>player:BV1xx:</c>.</param>
    /// <returns>释放条数.</returns>
    public int ReleaseAllOwnedByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return 0;
        }

        var count = 0;
        foreach (var pair in _registrations)
        {
            if (pair.Value.Source.OwnerKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                Release(pair.Key);
                count++;
            }
        }

        if (count > 0)
        {
            LogMessage(MediaProxyLogLevel.Debug, $"按归属键前缀释放 {count} 条登记");
        }

        return count;
    }

    /// <summary>
    /// 统计快照.
    /// </summary>
    /// <returns>统计.</returns>
    public MediaProxyStatistics GetStatistics()
        => new(
            Port,
            _registrations.Count,
            Interlocked.Read(ref _requestCount),
            Interlocked.Read(ref _rangeRequestCount),
            Interlocked.Read(ref _upstreamRequests),
            Interlocked.Read(ref _upstreamRetries),
            Interlocked.Read(ref _upstreamBytes),
            _cache.GetStatistics());

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 停止 = 释放全部登记（连带缓存租约）、断开在飞下载、清空缓存、等后台循环退出.
        Stop();
        _upstream.Dispose();
    }

    /// <summary>
    /// 按 MimeType 给播放器一个扩展名提示（只影响 URL 外观，代理解析时会被剥掉）.
    /// </summary>
    private static string GetUrlExtension(MediaStreamIdentity identity)
    {
        var mime = identity.MimeType;
        if (mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            return ".m4s";
        }

        if (mime.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase))
        {
            return ".m4a";
        }

        return string.Empty;
    }

    /// <summary>
    /// 剥掉登记 id 后面的扩展名提示.
    /// </summary>
    private static string StripExtension(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? segment[..dot] : segment;
    }

    private static string CreateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string Describe(MediaStreamSourceDescriptor descriptor)
    {
        var name = string.IsNullOrEmpty(descriptor.DisplayName) ? "-" : descriptor.DisplayName;
        return string.Create(CultureInfo.InvariantCulture, $"{name} [{descriptor.Identity.ToDiagnosticString()}] → {DescribeHost(descriptor.Url)}");
    }

    /// <summary>
    /// 只留 host + path 前 24 字符：足够定位问题，又不泄露签名参数.
    /// </summary>
    private static string DescribeHost(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "?";
        }

        var path = uri.AbsolutePath;
        var shortPath = path.Length <= 24 ? path : path[..24] + "…";
        return uri.Host + shortPath;
    }

    private void LogMessage(MediaProxyLogLevel level, string message)
    {
        try
        {
            Log?.Invoke(level, message);
        }
        catch
        {
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                LogMessage(MediaProxyLogLevel.Warning, "接受连接失败：" + ex.SocketErrorCode);
                await Task.Delay(20, CancellationToken.None);
                continue;
            }

            _ = HandleConnectionAsync(client);
        }
    }

    private async Task JanitorLoopAsync(CancellationToken token)
    {
        var interval = _options.JanitorInterval > TimeSpan.Zero ? _options.JanitorInterval : TimeSpan.FromMinutes(1);
        var idleLimit = _options.RegistrationIdleTimeout > TimeSpan.Zero ? _options.RegistrationIdleTimeout : TimeSpan.FromMinutes(3);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var now = NowTicks;
                var idleTicks = idleLimit.Ticks;
                foreach (var pair in _registrations)
                {
                    // 只回收「空闲超时」的登记；租约本身由登记持有，释放登记就把租约一起还回去了.
                    if (now - pair.Value.LastAccessTicks > idleTicks)
                    {
                        Release(pair.Key);
                        LogMessage(MediaProxyLogLevel.Debug, $"回收空闲登记 {pair.Key}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            LogMessage(MediaProxyLogLevel.Warning, "登记回收循环异常：" + ex.Message);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                RequestHead? head;
                using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ProxyToken))
                {
                    headerCts.CancelAfter(_options.RequestHeaderTimeout);
                    head = await ReadRequestHeadAsync(stream, headerCts.Token);
                }

                if (head is null)
                {
                    return;
                }

                Interlocked.Increment(ref _requestCount);

                // 请求方（播放器）中途断开时，在飞的上游块下载也要跟着断：
                // 挂一个「读等 FIN」的哨兵，读到 0 字节就把本次请求的 token 取消掉，
                // 于是块下载不会再把没人要的数据写进缓存.
                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ProxyToken);
                var watcher = WatchClientAsync(stream, connectionCts);
                try
                {
                    await DispatchAsync(stream, head, connectionCts.Token);
                }
                finally
                {
                    try
                    {
                        await connectionCts.CancelAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    try
                    {
                        await watcher;
                    }
                    catch
                    {
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                LogMessage(MediaProxyLogLevel.Warning, "处理请求异常：" + ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// 请求方断连哨兵：连接是全双工的，客户端在这条连接上不会再发任何数据，
    /// 所以这里有数据可读只有两种可能 —— 对端关闭（读到 0 字节）或非预期的管线化请求.
    /// 读到 0 就把本次请求的 token 取消，让在飞的块下载立刻停下（取消后不写缓存）.
    /// </summary>
    private static async Task WatchClientAsync(NetworkStream stream, CancellationTokenSource connectionCts)
    {
        var buffer = new byte[1];
        try
        {
            while (!connectionCts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, connectionCts.Token);
                if (read <= 0)
                {
                    try
                    {
                        await connectionCts.CancelAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task DispatchAsync(NetworkStream stream, RequestHead head, CancellationToken ct)
    {
        if (!string.Equals(head.Method, "GET", StringComparison.Ordinal)
            && !string.Equals(head.Method, "HEAD", StringComparison.Ordinal))
        {
            await WriteResponseHeadAsync(stream, 405, "Method Not Allowed", null, 0, null, ct);
            return;
        }

        var path = ExtractPath(head.Target);
        if (!path.StartsWith(PathRoot, StringComparison.Ordinal))
        {
            await WriteResponseHeadAsync(stream, 404, "Not Found", null, 0, null, ct);
            return;
        }

        var rest = path[PathRoot.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            await WriteResponseHeadAsync(stream, 404, "Not Found", null, 0, null, ct);
            return;
        }

        var token = rest[..slash];
        var id = StripExtension(rest[(slash + 1)..]);
        if (!IsTokenValid(token) || string.IsNullOrEmpty(id) || !_registrations.TryGetValue(id, out var registration))
        {
            // 未知 token / 未知 id：一律 404，不透露任何信息.
            await WriteResponseHeadAsync(stream, 404, "Not Found", null, 0, null, ct);
            return;
        }

        registration.Touch();
        if (string.Equals(head.Method, "HEAD", StringComparison.Ordinal))
        {
            await ServeHeadAsync(stream, registration, ct);
            return;
        }

        await ServeGetAsync(stream, head, registration, ct);
    }

    private bool IsTokenValid(string token)
    {
        var expected = Encoding.ASCII.GetBytes(_sessionToken);
        var actual = Encoding.ASCII.GetBytes(token);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string ExtractPath(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return "/";
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) && !absolute.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase))
        {
            return absolute.AbsolutePath;
        }

        var query = target.IndexOf('?', StringComparison.Ordinal);
        return query >= 0 ? target[..query] : target;
    }

    private async Task ServeHeadAsync(NetworkStream stream, MediaProxyRegistration registration, CancellationToken ct)
    {
        var probe = await EnsureProbeAsync(registration, ct);
        if (probe is null)
        {
            await WriteResponseHeadAsync(stream, 502, "Bad Gateway", null, 0, null, ct);
            return;
        }

        await WriteResponseHeadAsync(
            stream,
            probe.Value.SupportsRange ? 206 : 200,
            probe.Value.SupportsRange ? "Partial Content" : "OK",
            GetContentType(registration),
            probe.Value.Length,
            probe.Value.SupportsRange ? string.Create(CultureInfo.InvariantCulture, $"bytes 0-{Math.Max(0, probe.Value.Length - 1)}/{probe.Value.Length}") : null,
            ct);
    }

    private async Task ServeGetAsync(NetworkStream stream, RequestHead head, MediaProxyRegistration registration, CancellationToken ct)
    {
        var probe = await EnsureProbeAsync(registration, ct);
        if (probe is null)
        {
            await WriteResponseHeadAsync(stream, 502, "Bad Gateway", null, 0, null, ct);
            return;
        }

        var (total, supportsRange) = probe.Value;
        if (!supportsRange)
        {
            // 上游不支持 Range：整段透传（不缓存），语义上等价于把原地址交给播放器.
            await ServeWholeAsync(stream, registration, total, ct);
            return;
        }

        var parse = MediaByteRanges.TryParse(head.GetHeader("Range"), total, out var range);
        if (parse == MediaRangeParseResult.Unsatisfiable)
        {
            await WriteResponseHeadAsync(stream, 416, "Range Not Satisfiable", null, 0, string.Create(CultureInfo.InvariantCulture, $"bytes */{total}"), ct);
            return;
        }

        if (range.IsEmpty)
        {
            // 没带 Range（或无解的多段 Range）：整段 200，这是 RFC 7233 允许的行为.
            range = MediaByteRange.FromBounds(0, total);
            if (total == 0)
            {
                await WriteResponseHeadAsync(stream, 200, "OK", GetContentType(registration), 0, null, ct);
                return;
            }
        }
        else
        {
            Interlocked.Increment(ref _rangeRequestCount);
        }

        var status = range.Length == total && range.Start == 0 ? 200 : 206;
        var contentRange = status == 206
            ? string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.EndInclusive}/{total}")
            : null;
        await WriteResponseHeadAsync(stream, status, status == 206 ? "Partial Content" : "OK", GetContentType(registration), range.Length, contentRange, ct);
        await CopyRangeAsync(stream, registration, range, ct);
    }

    private async Task ServeWholeAsync(NetworkStream stream, MediaProxyRegistration registration, long total, CancellationToken ct)
    {
        await WriteResponseHeadAsync(stream, 200, "OK", GetContentType(registration), total, null, ct);
        using var request = CreateUpstreamRequest(registration, null);
        using var timeout = CreateUpstreamTimeout(ct);
        using var response = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Interlocked.Increment(ref _upstreamRequests);
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
        {
            LogMessage(MediaProxyLogLevel.Warning, "整段透传上游状态异常：" + (int)response.StatusCode);
            return;
        }

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await body.ReadAsync(buffer, timeout.Token);
            if (read <= 0)
            {
                break;
            }

            Interlocked.Add(ref _upstreamBytes, read);
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private async Task CopyRangeAsync(NetworkStream stream, MediaProxyRegistration registration, MediaByteRange range, CancellationToken ct)
    {
        var blockSize = _cache.BlockSize;
        var position = range.Start;
        while (position < range.EndExclusive)
        {
            var blockIndex = (int)(position / blockSize);
            var offsetInBlock = (int)(position % blockSize);
            var block = await GetBlockAsync(registration, blockIndex, ct);
            if (block is null)
            {
                // 拿不到就断开：让播放器自己重试（mpv 对断流会重连），不在这里编造数据.
                LogMessage(MediaProxyLogLevel.Warning, $"上游取块失败，断开连接（block={blockIndex}）");
                return;
            }

            var take = (int)Math.Min((long)block.Length - offsetInBlock, range.EndExclusive - position);
            if (take <= 0)
            {
                return;
            }

            await stream.WriteAsync(block.AsMemory(offsetInBlock, take), ct);
            position += take;
        }

        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// 取一个完整的块：内存命中直接给；否则走「同一块只下一次 flight」.
    /// <para>
    /// <paramref name="ct"/> 是请求方（播放器）的取消令牌：它一取消，这个等待者立刻退出，
    /// 而且如果它是这条 flight 的最后一个等待者，上游下载会被一起取消 —— 半截块不会被写进缓存，
    /// 下一次同一 range 的请求会重新开一条 flight 重新拉.
    /// </para>
    /// </summary>
    private async Task<byte[]?> GetBlockAsync(MediaProxyRegistration registration, int blockIndex, CancellationToken ct)
    {
        if (registration.Lease.TryGetBlock(blockIndex, out var cached))
        {
            registration.Lease.MarkServedFromCache(cached.Length);
            return cached;
        }

        var key = string.Concat(registration.Id, "#", blockIndex.ToString(CultureInfo.InvariantCulture));
        var flight = JoinFlight(key, registration, blockIndex);
        try
        {
            return await flight.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMessage(MediaProxyLogLevel.Warning, "取块异常：" + ex.GetType().Name);
            return null;
        }
        finally
        {
            var canceled = flight.Leave();
            // flight 结束（取消或完成）就从表里摘掉：摘掉后同 range 的新请求会重新开一条，
            // 不会挂到一条已经作废的 flight 上.
            if (canceled || flight.Task.IsCompleted)
            {
                RemoveFlight(key, flight);
            }

            if (canceled)
            {
                LogMessage(
                    MediaProxyLogLevel.Debug,
                    string.Create(CultureInfo.InvariantCulture, $"请求方已取消，断开在飞的上游下载（block={blockIndex}，不写缓存）"));
            }
        }
    }

    /// <summary>
    /// 加入（或新开）一条块下载 flight；撞上一条正在收尾（已取消）的 flight 时摘掉它重开.
    /// </summary>
    private BlockFlight JoinFlight(string key, MediaProxyRegistration registration, int blockIndex)
    {
        while (true)
        {
            var flight = _blockFlights.GetOrAdd(
                key,
                _ => new BlockFlight(flightToken => FetchBlockAsync(registration, blockIndex, flightToken), ProxyToken));
            if (flight.TryJoin())
            {
                return flight;
            }

            RemoveFlight(key, flight);
        }
    }

    /// <summary>
    /// 只摘掉「表里这条 key 上正好是这个实例」的 flight，别误删后来新开的.
    /// </summary>
    private void RemoveFlight(string key, BlockFlight flight)
        => ((ICollection<KeyValuePair<string, BlockFlight>>)_blockFlights).Remove(new KeyValuePair<string, BlockFlight>(key, flight));

    private async Task<byte[]?> FetchBlockAsync(MediaProxyRegistration registration, int blockIndex, CancellationToken ct)
    {
        if (registration.Length is not { } total)
        {
            return null;
        }

        var blockSize = _cache.BlockSize;
        var blockStart = (long)blockIndex * blockSize;
        if (blockStart >= total)
        {
            return null;
        }

        var blockLength = (int)Math.Min(blockSize, total - blockStart);
        var data = await DownloadRangeAsync(registration, blockStart, blockLength, ct);
        if (data is null)
        {
            return null;
        }

        if (data.Length != blockLength)
        {
            LogMessage(MediaProxyLogLevel.Warning, "上游返回长度不足，放弃该块（不缓存不完整数据）");
            return null;
        }

        // 取消与写入之间不能再有窗口：请求方已经走了就不许把数据写进缓存.
        ct.ThrowIfCancellationRequested();

        // 写缓存可能因为超预算失败；失败也照常返回数据，只是这次不缓存.
        _ = registration.Lease.TryWriteBlock(blockIndex, data);
        registration.Lease.MarkFetchedFromUpstream(data.Length);
        return data;
    }

    private async Task<byte[]?> DownloadRangeAsync(MediaProxyRegistration registration, long start, int length, CancellationToken ct)
    {
        var rangeValue = string.Create(CultureInfo.InvariantCulture, $"bytes={start}-{start + length - 1}");
        var maxAttempts = Math.Max(0, _options.MaxUpstreamRetries) + 1;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                Interlocked.Increment(ref _upstreamRetries);
                var delay = TimeSpan.FromMilliseconds(200 * (1 << (attempt - 1)));
                await Task.Delay(delay, ct);
            }

            try
            {
                using var request = CreateUpstreamRequest(registration, rangeValue);
                using var timeout = CreateUpstreamTimeout(ct);
                Interlocked.Increment(ref _upstreamRequests);
                using var response = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (IsRetryable(response.StatusCode))
                {
                    LogMessage(MediaProxyLogLevel.Warning, string.Create(CultureInfo.InvariantCulture, $"上游 {(int)response.StatusCode}，退避后重试（第 {attempt + 1} 次）"));
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var payload = await ReadExactAsync(response, length, timeout.Token);
                    if (payload is not null)
                    {
                        Interlocked.Add(ref _upstreamBytes, payload.Length);
                    }

                    return payload;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // 上游忽略了 Range：如果请求的就是文件开头，可以读前 length 字节；否则不能用.
                    if (start == 0)
                    {
                        var payload = await ReadExactAsync(response, length, timeout.Token);
                        if (payload is not null)
                        {
                            Interlocked.Add(ref _upstreamBytes, payload.Length);
                        }

                        return payload;
                    }

                    LogMessage(MediaProxyLogLevel.Warning, "上游忽略了 Range 请求，放弃该块");
                    return null;
                }

                LogMessage(MediaProxyLogLevel.Warning, "上游返回非预期状态：" + (int)response.StatusCode);
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // 单次上游超时：算一次失败，继续退避重试.
                LogMessage(MediaProxyLogLevel.Warning, "上游请求超时");
            }
            catch (HttpRequestException ex)
            {
                LogMessage(MediaProxyLogLevel.Warning, "上游请求失败：" + ex.HttpRequestError);
            }
            catch (IOException ex)
            {
                LogMessage(MediaProxyLogLevel.Warning, "上游读取中断：" + ex.GetType().Name);
            }
        }

        return null;
    }

    private static bool IsRetryable(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.PreconditionFailed or (HttpStatusCode)429;

    private static async Task<byte[]?> ReadExactAsync(HttpResponseMessage response, int length, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[length];
        var filled = 0;
        while (filled < length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(filled, length - filled), ct);
            if (read <= 0)
            {
                break;
            }

            filled += read;
        }

        if (filled != length)
        {
            return null;
        }

        return buffer;
    }

    private HttpRequestMessage CreateUpstreamRequest(MediaProxyRegistration registration, string? rangeValue)
    {
        var source = registration.Source;
        var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (rangeValue is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", rangeValue);
        }

        if (!string.IsNullOrEmpty(source.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", source.UserAgent);
        }

        if (!string.IsNullOrEmpty(source.Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", source.Referer);
        }

        if (!string.IsNullOrEmpty(source.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", source.Cookie);
        }

        return request;
    }

    private CancellationTokenSource CreateUpstreamTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.UpstreamTimeout);
        return cts;
    }

    /// <summary>
    /// 探测总长度与上游是否支持 Range；同一登记只探一次.
    /// </summary>
    private async Task<(long Length, bool SupportsRange)?> EnsureProbeAsync(MediaProxyRegistration registration, CancellationToken ct)
    {
        Task<bool> task;
        lock (registration.ProbeGate)
        {
            task = registration.ProbeTask ??= ProbeAsync(registration);
        }

        bool ok;
        try
        {
            ok = await task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            ok = false;
        }

        if (!ok || registration.Length is not { } length)
        {
            // 失败不保留探测结论，否则一次瞬时失败会让这条登记永远 502；
            // 下一个请求重新探测即可.
            lock (registration.ProbeGate)
            {
                if (registration.ProbeTask is { IsCompleted: true })
                {
                    registration.ProbeTask = null;
                }
            }

            return null;
        }

        return (length, registration.SupportsRange);
    }

    private async Task<bool> ProbeAsync(MediaProxyRegistration registration)
    {
        if (registration.Length is not null)
        {
            return true;
        }

        // 探测本身也要扛得住偶发 403/429：否则一次瞬时失败就会让整条播放链回落到原地址.
        var maxAttempts = Math.Max(0, _options.MaxUpstreamRetries) + 1;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                Interlocked.Increment(ref _upstreamRetries);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << (attempt - 1))), ProxyToken);
            }

            var result = await ProbeOnceAsync(registration);
            if (result is { } value)
            {
                return value;
            }
        }

        return false;
    }

    /// <summary>
    /// 探测一次；返回 null 表示「可重试的失败」.
    /// </summary>
    private async Task<bool?> ProbeOnceAsync(MediaProxyRegistration registration)
    {
        try
        {
            using var request = CreateUpstreamRequest(registration, "bytes=0-0");
            using var timeout = CreateUpstreamTimeout(ProxyToken);
            Interlocked.Increment(ref _upstreamRequests);
            using var response = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (IsRetryable(response.StatusCode))
            {
                LogMessage(MediaProxyLogLevel.Warning, string.Create(CultureInfo.InvariantCulture, $"探测遇到上游 {(int)response.StatusCode}，将退避重试"));
                return null;
            }

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var rangeHeader = response.Content.Headers.ContentRange;
                if (rangeHeader is { Length: { } total })
                {
                    registration.Lease.Length = total;
                    registration.SupportsRange = true;
                    return true;
                }

                return false;
            }

            if (response.StatusCode == (HttpStatusCode)416)
            {
                registration.Lease.Length = 0;
                registration.SupportsRange = true;
                return true;
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var total = response.Content.Headers.ContentLength;
                if (total is { } value && value >= 0)
                {
                    registration.Lease.Length = value;
                    registration.SupportsRange = false;
                    LogMessage(MediaProxyLogLevel.Information, "上游不支持 Range，降级为整段透传");
                    return true;
                }
            }

            LogMessage(MediaProxyLogLevel.Warning, "长度探测失败，上游状态：" + (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (ProxyToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            LogMessage(MediaProxyLogLevel.Warning, "长度探测超时");
            return null;
        }
        catch (Exception ex)
        {
            LogMessage(MediaProxyLogLevel.Warning, "长度探测异常：" + ex.GetType().Name);
            return null;
        }
    }

    private static string GetContentType(MediaProxyRegistration registration)
        => string.IsNullOrEmpty(registration.Identity.MimeType) ? "application/octet-stream" : registration.Identity.MimeType;

    private async Task WriteResponseHeadAsync(
        NetworkStream stream,
        int status,
        string reason,
        string? contentType,
        long? contentLength,
        string? contentRange,
        CancellationToken ct)
    {
        var builder = new StringBuilder(256);
        builder.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
        builder.Append("Server: BiliCopilot-MediaProxy\r\n");
        builder.Append("Accept-Ranges: bytes\r\n");
        builder.Append("Cache-Control: no-store\r\n");
        builder.Append("Connection: close\r\n");
        if (contentType is not null)
        {
            builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
        }

        if (contentRange is not null)
        {
            builder.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        }

        if (contentLength is { } length)
        {
            builder.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        builder.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<RequestHead?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var accumulated = new List<byte>(1024);
        while (accumulated.Count <= MaxHeaderBytes)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), ct);
            }
            catch (IOException)
            {
                return null;
            }

            if (read <= 0)
            {
                return null;
            }

            for (var i = 0; i < read; i++)
            {
                accumulated.Add(buffer[i]);
            }

            var end = FindHeaderEnd(accumulated);
            if (end >= 0)
            {
                return ParseRequestHead(accumulated, end);
            }
        }

        return null;
    }

    private static int FindHeaderEnd(List<byte> data)
    {
        for (var i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' && data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static RequestHead? ParseRequestHead(List<byte> data, int headerEnd)
    {
        var text = Encoding.ASCII.GetString(data.ToArray(), 0, headerEnd);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new RequestHead(requestLine[0], requestLine[1], requestLine.Length > 2 ? requestLine[2] : "HTTP/1.1", headers);
    }

    /// <summary>
    /// 一个块的在飞下载：多个请求共享同一条 flight；最后一个等待者离开时取消上游下载
    /// （取消后不写缓存，下一次同一 range 的请求会重开一条 flight 重新拉）.
    /// </summary>
    private sealed class BlockFlight
    {
        private readonly CancellationTokenSource _cts;
        private readonly object _gate = new();

        private int _waiters;
        private bool _closed;

        public BlockFlight(Func<CancellationToken, Task<byte[]?>> body, CancellationToken proxyToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(proxyToken);
            Task = body(_cts.Token);
        }

        public Task<byte[]?> Task { get; }

        /// <summary>
        /// 加一个等待者；这条 flight 已经在收尾（被取消）时返回 false，调用方应重开一条.
        /// </summary>
        public bool TryJoin()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }

                _waiters++;
                return true;
            }
        }

        /// <summary>
        /// 一个等待者离开；返回 true 表示它是最后一个走的，并且上游下载因此被取消.
        /// </summary>
        public bool Leave()
        {
            lock (_gate)
            {
                if (_waiters > 0)
                {
                    _waiters--;
                }

                // 还有人等、已经收尾、或者已经跑完（成功失败都算）→ 都不用取消.
                if (_waiters > 0 || _closed || Task.IsCompleted)
                {
                    return false;
                }

                _closed = true;
            }

            Cancel();
            return true;
        }

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// 一次请求的头（只取代理需要的部分）.
    /// </summary>
    private sealed class RequestHead(string method, string target, string version, Dictionary<string, string> headers)
    {
        public string Method { get; } = method;

        public string Target { get; } = target;

        public string Version { get; } = version;

        private Dictionary<string, string> Headers { get; } = headers;

        public string GetHeader(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
