// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 预取阶段。数字即「目标秒数档位」，方便按设置里的秒数上限裁剪。
/// </summary>
public enum MediaPrefetchStage
{
    /// <summary>
    /// 没指定阶段（按自定义秒数预取，例如 E 的按时间点空降）.
    /// </summary>
    None = 0,

    /// <summary>
    /// 阶段一：够「立刻可播」——init + index + 前 3 秒.
    /// </summary>
    Immediate = 3,

    /// <summary>
    /// 阶段二：前 10 秒.
    /// </summary>
    Short = 10,

    /// <summary>
    /// 阶段三：前 30 秒.
    /// </summary>
    Extended = 30,
}

/// <summary>
/// 预取可调参数.
/// </summary>
public sealed class MediaPrefetchOptions
{
    /// <summary>
    /// 全局并发上限（同时最多几个上游区间请求在飞），默认 2.
    /// </summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>
    /// 预取账本预算（字节），默认 256 MiB。超了按 LRU 释放最冷的目标。
    /// 这是「预取自己认的账」，缓存容量由 <see cref="MediaProxyOptions.CapacityBytes"/> 管，
    /// 两者互不替代：缓存容量管内存，这里的预算管「值不值得继续往前拉」。
    /// </summary>
    public long BudgetBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// 未知码率、也没有 SIDX 时，阶段一至少拉多少字节（够探测出真实速率）.
    /// </summary>
    public long FallbackProbeBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// 单个上游区间请求的超时.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 长度探测（HEAD）超时.
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 同一个目标最多同时跑几个阶段（现在只串行跑，留个上限防将来并发）.
    /// </summary>
    public int MaxTargets { get; set; } = 12;
}

/// <summary>
/// 预取目标的元信息（来自 dash 段：标称带宽、init/index 字节区间、总时长）.
/// </summary>
public sealed class MediaPrefetchSourceInfo
{
    /// <summary>
    /// 日志显示名（建议「标题 · 分P · 画质」，不要放 URL）.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// dash 段标称带宽（bps）；未知时填 0，此时会按实测速率自适应.
    /// </summary>
    public long BandwidthBps { get; set; }

    /// <summary>
    /// dash 的 Initialization 字节区间（moov）.
    /// </summary>
    public MediaByteRange? InitializationRange { get; set; }

    /// <summary>
    /// dash 的 IndexRange 字节区间（sidx）.
    /// </summary>
    public MediaByteRange? IndexRange { get; set; }

    /// <summary>
    /// 媒体总时长（秒），用于「按时间点预取」的估算兜底.
    /// </summary>
    public double? DurationSeconds { get; set; }
}

/// <summary>
/// 一次预取的计算结果（目标窗口 + 还缺多少）.
/// </summary>
/// <param name="Stage">阶段.</param>
/// <param name="StageSeconds">阶段目标秒数.</param>
/// <param name="TargetBytes">本阶段窗口的总字节数.</param>
/// <param name="MissingBytes">窗口里还缺（没缓存）的字节数.</param>
/// <param name="Windows">需要覆盖的字节窗口（已合并、已裁剪到资源长度）.</param>
/// <param name="IsEstimate">秒→字节是否来自估算（不是 SIDX）.</param>
/// <param name="Source">换算依据：sidx / bandwidth / measured / probe.</param>
/// <param name="Note">补充说明（进日志）.</param>
public readonly record struct MediaPrefetchPlan(
    MediaPrefetchStage Stage,
    int StageSeconds,
    long TargetBytes,
    long MissingBytes,
    IReadOnlyList<MediaByteRange> Windows,
    bool IsEstimate,
    string Source,
    string Note);

/// <summary>
/// 预取进度（阶段内逐块推进时回调；在后台线程触发，别在里面碰 UI）.
/// </summary>
/// <param name="Stage">阶段.</param>
/// <param name="StageSeconds">阶段目标秒数.</param>
/// <param name="TargetBytes">本阶段窗口字节数.</param>
/// <param name="ReceivedBytes">本阶段真正从上游拿到的字节数.</param>
/// <param name="CompletedBytes">本阶段已经处理完（含命中）的字节数.</param>
/// <param name="IsEstimate">是否估算.</param>
public readonly record struct MediaPrefetchProgress(
    MediaPrefetchStage Stage,
    int StageSeconds,
    long TargetBytes,
    long ReceivedBytes,
    long CompletedBytes,
    bool IsEstimate);

/// <summary>
/// 一次预取的结果.
/// </summary>
/// <param name="Success">是否完成（没有失败也没有被取消）.</param>
/// <param name="Skipped">是否跳过（上游不支持 Range / 探测失败 / 预算不够）.</param>
/// <param name="Cancelled">是否被取消.</param>
/// <param name="Stage">阶段.</param>
/// <param name="TargetBytes">目标字节.</param>
/// <param name="ReceivedBytes">实得字节.</param>
/// <param name="AlreadyCachedBytes">阶段开始时已经缓存的字节（命中）.</param>
/// <param name="ElapsedMilliseconds">耗时.</param>
/// <param name="IsEstimate">秒→字节是否估算.</param>
/// <param name="Note">说明.</param>
public readonly record struct MediaPrefetchOutcome(
    bool Success,
    bool Skipped,
    bool Cancelled,
    MediaPrefetchStage Stage,
    long TargetBytes,
    long ReceivedBytes,
    long AlreadyCachedBytes,
    long ElapsedMilliseconds,
    bool IsEstimate,
    string Note)
{
    /// <summary>
    /// 命中率（0~1）：本阶段目标里开局就已经在缓存中的比例.
    /// </summary>
    public double HitRate => TargetBytes <= 0 ? 0 : Math.Clamp(AlreadyCachedBytes / (double)TargetBytes, 0, 1);
}

/// <summary>
/// 预取统计快照（给诊断面板 / F 验收用）.
/// </summary>
/// <param name="RequestedBytes">主动请求的字节总数（块对齐后）.</param>
/// <param name="ReceivedBytes">实际从上游读到并写进缓存的字节总数.</param>
/// <param name="UsefulHitBytes">预取开启后，缓存被读走（播放用上）的字节总数.</param>
/// <param name="WastedBytes">预取了但没被用上就释放 / 淘汰的字节（估算值）.</param>
/// <param name="BudgetBytes">预取账本预算.</param>
/// <param name="TrackedBytes">当前存活目标占的预取字节.</param>
/// <param name="ActiveTargets">当前存活目标数.</param>
/// <param name="EntryCount">缓存里的条目数.</param>
/// <param name="CacheCurrentBytes">缓存当前占用字节.</param>
/// <param name="CompletedStages">完成过的阶段数.</param>
/// <param name="CancelledStages">被取消的阶段数.</param>
/// <param name="EvictionCount">因预算触发的目标淘汰次数.</param>
public readonly record struct MediaPrefetchStatistics(
    long RequestedBytes,
    long ReceivedBytes,
    long UsefulHitBytes,
    long WastedBytes,
    long BudgetBytes,
    long TrackedBytes,
    int ActiveTargets,
    int EntryCount,
    long CacheCurrentBytes,
    int CompletedStages,
    int CancelledStages,
    int EvictionCount)
{
    /// <inheritdoc/>
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"req={RequestedBytes / 1024}KiB got={ReceivedBytes / 1024}KiB used={UsefulHitBytes / 1024}KiB wasted={WastedBytes / 1024}KiB tracked={TrackedBytes / 1024}KiB budget={BudgetBytes / 1024 / 1024}MiB targets={ActiveTargets} entries={EntryCount} cache={CacheCurrentBytes / 1024}KiB stages={CompletedStages} cancelled={CancelledStages} evictions={EvictionCount}");
}

/// <summary>
/// 一个预取目标：一条已登记的代理源 + 用于换算秒→字节的 dash 元信息。
/// <para>
/// 目标持有代理登记（<see cref="MediaProxyRegistration"/>）。由本服务
/// <see cref="MediaPrefetchService.RegisterTarget"/> 创建的目标归服务所有（释放时一并结束登记）；
/// 由调用方 <see cref="MediaPrefetchService.AttachTarget"/> 挂上来的登记不归服务所有，
/// 服务只借用，不会替调用方释放（播放链路自己还留着那份登记）。
/// </para>
/// </summary>
public sealed class MediaPrefetchTarget
{
    private readonly MediaBufferProxy _proxy;

    internal MediaPrefetchTarget(
        MediaBufferProxy proxy,
        MediaProxyRegistration registration,
        bool ownsRegistration,
        MediaPrefetchSourceInfo info,
        long sequence)
    {
        _proxy = proxy;
        Registration = registration;
        OwnsRegistration = ownsRegistration;
        Sequence = sequence;
        ApplyInfo(info);
        HitBaseline = proxy.Cache.GetStatistics().HitBytes;
        LastActivityTicks = Environment.TickCount64;
    }

    /// <summary>
    /// 登记 id.
    /// </summary>
    public string Id => Registration.Id;

    /// <summary>
    /// 代理地址（播放器可用；预取内部也走这条地址，这样并发拉取天然复用代理的 flight 合并）.
    /// </summary>
    public string ProxyUrl => Registration.ProxyUrl;

    /// <summary>
    /// 逻辑媒体身份（缓存 key）.
    /// </summary>
    public MediaStreamIdentity Identity => Registration.Identity;

    /// <summary>
    /// 视频 BV 号（番剧是 epId / seasonId）.
    /// </summary>
    public string Bvid => Identity.Bvid;

    /// <summary>
    /// 分 P 的 cid.
    /// </summary>
    public string Cid => Identity.Cid;

    /// <summary>
    /// 归属键（不含预取前缀）.
    /// </summary>
    public string OwnerKey { get; private set; } = string.Empty;

    /// <summary>
    /// 日志显示名.
    /// </summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// dash 标称带宽（bps），0 表示未知.
    /// </summary>
    public long BandwidthBps { get; internal set; }

    /// <summary>
    /// dash 的 Initialization 字节区间.
    /// </summary>
    public MediaByteRange? InitializationRange { get; private set; }

    /// <summary>
    /// dash 的 IndexRange 字节区间.
    /// </summary>
    public MediaByteRange? IndexRange { get; private set; }

    /// <summary>
    /// 媒体总时长（秒），可为 null.
    /// </summary>
    public double? DurationSeconds { get; private set; }

    /// <summary>
    /// 解析出来的 SIDX；没拿到或解析失败时为 null（此时秒→字节走估算）.
    /// </summary>
    public MediaSegmentIndex? SegmentIndex { get; internal set; }

    /// <summary>
    /// 实测速率（bps），按实际下载速率做指数滑动平均；未知为 null.
    /// </summary>
    public long? MeasuredBps { get; internal set; }

    /// <summary>
    /// 已经完成的最大阶段秒数.
    /// </summary>
    public int CompletedStageSeconds { get; internal set; }

    /// <summary>
    /// 本目标累计预取到的字节（内部按 <see cref="Interlocked"/> 累加）.
    /// </summary>
    internal long PrefetchedBytes;

    /// <summary>
    /// 本目标累计请求的字节（内部按 <see cref="Interlocked"/> 累加）.
    /// </summary>
    internal long RequestedBytes;

    /// <summary>
    /// 最近一次活动时间（用于 LRU 淘汰）.
    /// </summary>
    internal long LastActivityTicks { get; set; }

    /// <summary>
    /// 目标序号（越大越新）.
    /// </summary>
    internal long Sequence { get; }

    /// <summary>
    /// 创建时的缓存命中基线，用来算这个目标「贡献了多少有效命中」.
    /// </summary>
    internal long HitBaseline { get; }

    /// <summary>
    /// 是否归本服务所有.
    /// </summary>
    internal bool OwnsRegistration { get; }

    /// <summary>
    /// 正在飞的请求数（字段，供 <see cref="Interlocked"/> 用）.
    /// </summary>
    internal int InFlight;

    internal MediaProxyRegistration Registration { get; }

    internal void ApplyInfo(MediaPrefetchSourceInfo info)
    {
        DisplayName = string.IsNullOrEmpty(info.DisplayName) ? Identity.Bvid : info.DisplayName;
        BandwidthBps = info.BandwidthBps;
        InitializationRange = info.InitializationRange?.IsEmpty == false ? info.InitializationRange : null;
        IndexRange = info.IndexRange?.IsEmpty == false ? info.IndexRange : null;
        DurationSeconds = info.DurationSeconds;
        OwnerKey = StripOwnerPrefix(Registration.Source.OwnerKey);
    }

    /// <summary>
    /// 当前缓存里已经有多少这个目标的字节（给「点击时已有 N 字节缓存」用）.
    /// </summary>
    /// <returns>字节数.</returns>
    public long GetCachedBytes()
    {
        var length = Registration.Lease.Length;
        if (length is not { } total || total <= 0)
        {
            return 0;
        }

        var present = Registration.Lease.GetPresentRanges(new MediaByteRange(0, total));
        long sum = 0;
        foreach (var range in present)
        {
            sum += range.Length;
        }

        return sum;
    }

    private static string StripOwnerPrefix(string? ownerKey)
    {
        if (string.IsNullOrEmpty(ownerKey))
        {
            return string.Empty;
        }

        return ownerKey.StartsWith(MediaPrefetchService.PrefetchOwnerPrefix, StringComparison.Ordinal)
            ? ownerKey[MediaPrefetchService.PrefetchOwnerPrefix.Length..]
            : ownerKey;
    }
}

/// <summary>
/// 主动填充 API：把「开头若干块」提前拉进 <see cref="MediaRangeCache"/>，让点击播放时首帧不再等网络。
/// <para>
/// 这一层刻意只做网络字节层：不建 mpv 实例、不建渲染表面、不解码。
/// 它和 C 的代理共用同一条通道 —— 预取请求也是打到 <c>127.0.0.1</c> 的代理上，
/// 所以「同一块只下一次」的 flight 合并、块级缓存写入、LRU 淘汰全是现成的，
/// 不做第二套下载器，也就不会出现「预取和播放各拉一份」的双倍流量。
/// </para>
/// <para>
/// 依赖：<see cref="MediaBufferProxy"/>（已启动）、<see cref="MediaRangeCache"/>、
/// <see cref="MediaStreamIdentity"/>、<see cref="MediaSegmentIndex"/>、<see cref="MediaByteRange"/>。
/// 刻意不依赖 WinUI / Microsoft.Extensions.Logging，方便 tests/MediaCache.Tests 直接编译本文件跑。
/// </para>
/// </summary>
public sealed class MediaPrefetchService : IDisposable
{
    /// <summary>
    /// 预取登记的归属键前缀：预取登记和播放登记刻意分开，
    /// 释放预取登记时不会把正在播的那条登记一起收掉.
    /// </summary>
    public const string PrefetchOwnerPrefix = "prefetch:";

    private readonly MediaBufferProxy _proxy;
    private readonly MediaPrefetchOptions _options;
    private readonly HttpClient _client;
    private readonly object _gate = new();
    private readonly List<MediaPrefetchTarget> _targets = [];

    private long _sequence;
    private long _requestedBytes;
    private long _receivedBytes;
    private long _wastedBytes;
    private long _hitBaseline;
    private int _completedStages;
    private int _cancelledStages;
    private int _evictionCount;
    private int _inFlight;
    private int _paused;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaPrefetchService"/> class.
    /// </summary>
    /// <param name="proxy">已经启动的媒体代理（缓存挂在它身上）.</param>
    /// <param name="options">可调参数.</param>
    public MediaPrefetchService(MediaBufferProxy proxy, MediaPrefetchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _proxy = proxy;
        _options = options ?? new MediaPrefetchOptions();
        var handler = new SocketsHttpHandler
        {
            // 和代理一样：媒体字节绝不能被解压，否则长度与偏移全乱.
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = Math.Max(2, _options.MaxConcurrent),
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _hitBaseline = proxy.Cache.GetStatistics().HitBytes;
    }

    /// <summary>
    /// 日志回调；为 null 时不记日志。级别语义与代理一致.
    /// </summary>
    public MediaProxyLogHandler? Log { get; set; }

    /// <summary>
    /// 可调参数（<see cref="MediaPrefetchOptions.MaxConcurrent"/> 与
    /// <see cref="MediaPrefetchOptions.BudgetBytes"/> 支持运行期改）.
    /// </summary>
    public MediaPrefetchOptions Options => _options;

    /// <summary>
    /// 是否处于暂停（暂停时不再发起新的区间请求，正在飞的会跑完）.
    /// </summary>
    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    /// <summary>
    /// 当前存活目标（只读快照）.
    /// </summary>
    public IReadOnlyList<MediaPrefetchTarget> Targets
    {
        get
        {
            lock (_gate)
            {
                return _targets.ToArray();
            }
        }
    }

    /// <summary>
    /// 暂停所有预取（进播放、切到后台等场景）.
    /// </summary>
    public void Pause() => Volatile.Write(ref _paused, 1);

    /// <summary>
    /// 恢复预取.
    /// </summary>
    public void Resume() => Volatile.Write(ref _paused, 0);

    /// <summary>
    /// 登记一个代理源并开始预取它。
    /// 登记会带上 <see cref="PrefetchOwnerPrefix"/>，因此和播放链路自己的登记互不干扰。
    /// </summary>
    /// <param name="source">源描述（URL + 逻辑身份 + 鉴权头）.</param>
    /// <param name="info">dash 元信息，可为 null.</param>
    /// <returns>预取目标；同一源重复登记会拿到同一个目标.</returns>
    public MediaPrefetchTarget RegisterTarget(MediaStreamSourceDescriptor source, MediaPrefetchSourceInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_proxy.IsRunning)
        {
            throw new InvalidOperationException("媒体字节范围代理还没启动，无法预取.");
        }

        var value = info ?? new MediaPrefetchSourceInfo();
        var descriptor = new MediaStreamSourceDescriptor(source.Url, source.Identity)
        {
            Cookie = source.Cookie,
            Referer = source.Referer,
            UserAgent = source.UserAgent,
            DisplayName = string.IsNullOrEmpty(source.DisplayName) ? value.DisplayName : source.DisplayName,
            OwnerKey = PrefetchOwnerPrefix + (source.OwnerKey ?? string.Empty),
        };
        var registration = _proxy.Register(descriptor);
        return AttachCore(registration, value, ownsRegistration: true);
    }

    /// <summary>
    /// 挂一条已经登记好的代理源（调用方自己 <c>proxy.Register</c> 拿到的句柄）。
    /// 服务只借用，不会替你释放这条登记。
    /// </summary>
    /// <param name="registration">代理登记.</param>
    /// <param name="info">dash 元信息，可为 null.</param>
    /// <returns>预取目标.</returns>
    public MediaPrefetchTarget AttachTarget(MediaProxyRegistration registration, MediaPrefetchSourceInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return AttachCore(registration, info ?? new MediaPrefetchSourceInfo(), ownsRegistration: false);
    }

    /// <summary>
    /// 更新目标的 dash 元信息（预解析后补上带宽 / init / index / 时长）.
    /// </summary>
    /// <param name="target">目标.</param>
    /// <param name="info">元信息.</param>
    public void UpdateSourceInfo(MediaPrefetchTarget target, MediaPrefetchSourceInfo info)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(info);
        lock (_gate)
        {
            if (!_targets.Contains(target))
            {
                return;
            }

            target.ApplyInfo(info);
            target.LastActivityTicks = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 释放一个目标（取消它正在跑的预取；归服务所有的登记一并结束）.
    /// </summary>
    /// <param name="target">目标.</param>
    public void ReleaseTarget(MediaPrefetchTarget target)
    {
        if (target is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_targets.Remove(target))
            {
                return;
            }

            RecordWasteLocked(target);
        }

        if (target.OwnsRegistration)
        {
            try
            {
                target.Registration.Dispose();
            }
            catch
            {
            }
        }

        LogMessage(MediaProxyLogLevel.Debug, $"预取目标释放：{Describe(target)}");
    }

    /// <summary>
    /// 释放某个归属键下的全部预取目标（页面关闭 / 切走时用）.
    /// </summary>
    /// <param name="ownerKey">归属键（不含预取前缀）.</param>
    /// <returns>释放条数.</returns>
    public int ReleaseOwner(string ownerKey)
    {
        if (string.IsNullOrEmpty(ownerKey))
        {
            return 0;
        }

        List<MediaPrefetchTarget> victims;
        lock (_gate)
        {
            victims = [.. _targets.Where(p => string.Equals(p.OwnerKey, ownerKey, StringComparison.Ordinal))];
        }

        foreach (var victim in victims)
        {
            ReleaseTarget(victim);
        }

        // 兜底：登记表里可能还有已经不在 _targets 的（例如被淘汰后登记还挂在代理上）.
        var released = _proxy.ReleaseAllOwnedBy(PrefetchOwnerPrefix + ownerKey);
        if (victims.Count > 0 || released > 0)
        {
            LogMessage(MediaProxyLogLevel.Information, $"预取按归属键释放：owner={ownerKey} 目标 {victims.Count} 条，登记 {released} 条");
        }

        return victims.Count;
    }

    /// <summary>
    /// 计算某个阶段的预取窗口（不发起任何请求，纯计算，便于诊断与测试）.
    /// </summary>
    /// <param name="target">目标.</param>
    /// <param name="stage">阶段.</param>
    /// <param name="maxSeconds">设置里的秒数上限（0 / 负数表示不裁剪）.</param>
    /// <returns>计划.</returns>
    public MediaPrefetchPlan BuildPlan(MediaPrefetchTarget target, MediaPrefetchStage stage, int maxSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        var seconds = (int)stage;
        if (maxSeconds > 0 && seconds > maxSeconds)
        {
            seconds = maxSeconds;
        }

        return BuildPlanCore(target, stage, Math.Clamp(seconds, 1, 600));
    }

    /// <summary>
    /// 按自定义秒数预取（E 的按时间点空降也走这条，只是起点不同）.
    /// </summary>
    /// <param name="target">目标.</param>
    /// <param name="seconds">目标秒数.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <param name="progress">进度回调，可为 null.</param>
    /// <returns>结果.</returns>
    public Task<MediaPrefetchOutcome> PrefetchSecondsAsync(
        MediaPrefetchTarget target,
        int seconds,
        CancellationToken cancellationToken,
        IProgress<MediaPrefetchProgress>? progress = null)
        => PrefetchCoreAsync(target, MediaPrefetchStage.None, Math.Clamp(seconds, 1, 600), 0, cancellationToken, progress);

    /// <summary>
    /// 按阶段预取（阶段一 → 二 → 三，调用方按顺序调即可；重复调用只补缺失块）.
    /// </summary>
    /// <param name="target">目标.</param>
    /// <param name="stage">阶段.</param>
    /// <param name="maxSeconds">设置里的秒数上限（0 表示不裁剪）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <param name="progress">进度回调，可为 null.</param>
    /// <returns>结果.</returns>
    public Task<MediaPrefetchOutcome> PrefetchAsync(
        MediaPrefetchTarget target,
        MediaPrefetchStage stage,
        int maxSeconds,
        CancellationToken cancellationToken,
        IProgress<MediaPrefetchProgress>? progress = null)
        => PrefetchCoreAsync(target, stage, 0, maxSeconds, cancellationToken, progress);

    /// <summary>
    /// 「按时间点预取」：把某个播放位置（例如空降点）附近的字节先拉进缓存.
    /// <para>
    /// 先用 IndexRange 里的 SIDX 把时间精确映射到子段（<c>精确</c>）；
    /// 拿不到 SIDX 时退回「码率 × 时间」估算（<c>估算</c>，日志里会标出来）。
    /// </para>
    /// </summary>
    /// <param name="target">目标.</param>
    /// <param name="position">播放位置（从 0 开始）.</param>
    /// <param name="windowSeconds">要覆盖多少秒（默认 6 秒，前后各半）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <param name="progress">进度回调，可为 null.</param>
    /// <returns>结果.</returns>
    public async Task<MediaPrefetchOutcome> PrefetchAtTimeAsync(
        MediaPrefetchTarget target,
        TimeSpan position,
        int windowSeconds,
        CancellationToken cancellationToken,
        IProgress<MediaPrefetchProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        windowSeconds = Math.Clamp(windowSeconds, 1, 600);
        var started = Stopwatch.GetTimestamp();
        if (!await EnsureProbedAsync(target, cancellationToken).ConfigureAwait(false))
        {
            return NotProbed(target, MediaPrefetchStage.None, started);
        }

        if (!target.Registration.SupportsRange)
        {
            return Skipped(target, MediaPrefetchStage.None, "上游不支持 Range，跳过预取", started);
        }

        await EnsureSegmentIndexAsync(target, cancellationToken).ConfigureAwait(false);

        var total = target.Registration.Lease.Length ?? long.MaxValue;
        var blockSize = _proxy.Cache.BlockSize;
        var mediaStart = ResolveMediaStart(target);
        var positionSeconds = Math.Max(0, position.TotalSeconds - windowSeconds / 2.0);
        var isEstimate = true;
        var source = "bandwidth";
        long start;
        long length;

        if (target.SegmentIndex is { } index && index.TryMapTime(positionSeconds, mediaStart, out start, out length))
        {
            isEstimate = false;
            source = "sidx";
        }
        else
        {
            // 没有 SIDX：按「实际字节率」估。优先用「(总长度 - 媒体起点) / 总时长」这个真实平均字节率，
            // 它比标称码率更接近真实文件（标称码率是上限值，实际常偏低）；没有总时长才退到码率。
            var bytesPerSecond = ResolveBytesPerSecond(target);
            if (bytesPerSecond <= 0)
            {
                return Skipped(target, MediaPrefetchStage.None, "既没有 SIDX 也没有码率，无法把时间点换算成字节", started);
            }

            source = target.DurationSeconds is > 0 && target.Registration.Lease.Length is > 0
                ? "duration"
                : target.BandwidthBps > 0 ? "bandwidth" : "measured";
            start = mediaStart + AlignDown((long)Math.Ceiling(bytesPerSecond * positionSeconds), blockSize);
            length = (long)Math.Ceiling(bytesPerSecond * windowSeconds);
        }

        // 覆盖整段：从子段起点再多拉 windowSeconds 对应的字节（SIDX 时按子段逐个累加）.
        var windowEnd = start + Math.Max(length, (long)Math.Ceiling(ResolveBytesPerSecond(target) * windowSeconds));
        var window = MediaByteRange.FromBounds(start, Math.Min(windowEnd, total));
        window = AlignWindow(window, blockSize, total);

        var plan = new MediaPrefetchPlan(
            MediaPrefetchStage.None,
            windowSeconds,
            window.Length,
            SumMissingBytes(target, new[] { window }),
            new[] { window },
            isEstimate,
            source,
            string.Create(CultureInfo.InvariantCulture, $"position={position.TotalSeconds:F2}s"));

        return await RunPlanAsync(target, plan, started, cancellationToken, progress).ConfigureAwait(false);
    }

    /// <summary>
    /// 取统计快照.
    /// </summary>
    /// <returns>统计.</returns>
    public MediaPrefetchStatistics GetStatistics()
    {
        var cache = _proxy.Cache.GetStatistics();
        lock (_gate)
        {
            long tracked = 0;
            foreach (var target in _targets)
            {
                tracked += target.PrefetchedBytes;
            }

            return new MediaPrefetchStatistics(
                Interlocked.Read(ref _requestedBytes),
                Interlocked.Read(ref _receivedBytes),
                Math.Max(0, cache.HitBytes - _hitBaseline),
                // 只算「确实白拉了」的字节：被淘汰 / 主动释放、且没有被播放读走过。
                // 存活目标里暂时没被读的部分不算浪费（后面还有机会命中）。
                Interlocked.Read(ref _wastedBytes),
                _options.BudgetBytes,
                tracked,
                _targets.Count,
                cache.EntryCount,
                cache.CurrentBytes,
                _completedStages,
                _cancelledStages,
                _evictionCount);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<MediaPrefetchTarget> targets;
        lock (_gate)
        {
            targets = [.. _targets];
            _targets.Clear();
        }

        foreach (var target in targets)
        {
            if (target.OwnsRegistration)
            {
                try
                {
                    target.Registration.Dispose();
                }
                catch
                {
                }
            }
        }

        _client.Dispose();
    }

    private MediaPrefetchTarget AttachCore(MediaProxyRegistration registration, MediaPrefetchSourceInfo info, bool ownsRegistration)
    {
        lock (_gate)
        {
            var existing = _targets.FirstOrDefault(p => string.Equals(p.Id, registration.Id, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.ApplyInfo(info);
                existing.LastActivityTicks = Environment.TickCount64;
                return existing;
            }

            var target = new MediaPrefetchTarget(_proxy, registration, ownsRegistration, info, ++_sequence);
            _targets.Add(target);
            LogMessage(
                MediaProxyLogLevel.Debug,
                $"预取目标登记：{Describe(target)}（带宽 {(target.BandwidthBps > 0 ? (target.BandwidthBps / 1000).ToString(CultureInfo.InvariantCulture) + " Kbps" : "未知")}）");
            return target;
        }
    }

    private MediaPrefetchPlan BuildPlanCore(MediaPrefetchTarget target, MediaPrefetchStage stage, int seconds)
    {
        var total = target.Registration.Lease.Length ?? long.MaxValue;
        var blockSize = _proxy.Cache.BlockSize;
        var windows = new List<MediaByteRange>();

        // 1) init 段（moov）：mpv 必须先拿到它才能建轨道，永远是第一优先级.
        if (target.InitializationRange is { } init)
        {
            windows.Add(init);
        }

        // 2) index 段（sidx）：紧跟在 init 后面，取 SIDX 时也要它.
        if (target.IndexRange is { } index)
        {
            windows.Add(index);
        }

        var mediaStart = ResolveMediaStart(target);
        var isEstimate = true;
        var source = "sidx";
        long mediaBytes;

        if (target.SegmentIndex is { } segmentIndex)
        {
            mediaBytes = segmentIndex.GetByteLengthForSeconds(seconds);
            isEstimate = false;
        }
        else
        {
            var bps = target.BandwidthBps > 0 ? target.BandwidthBps : target.MeasuredBps ?? 0;
            if (bps > 0)
            {
                source = target.BandwidthBps > 0 ? "bandwidth" : "measured";
                mediaBytes = (long)Math.Ceiling(bps / 8.0 * seconds);
            }
            else
            {
                // 什么都没有：至少拉一段出去，用实测速率把后面的阶段修正回来.
                source = "probe";
                mediaBytes = Math.Max(blockSize * 2L, _options.FallbackProbeBytes);
            }
        }

        if (mediaBytes > 0)
        {
            var end = AlignUp(mediaStart + mediaBytes, blockSize);
            windows.Add(MediaByteRange.FromBounds(mediaStart, Math.Min(end, total)));
        }

        if (windows.Count == 0)
        {
            // init / index / 媒体起点全都没有：从 0 开始拉首块，至少不让阶段一是空转.
            windows.Add(MediaByteRange.FromBounds(0, Math.Min((long)blockSize * 2, total)));
            source = "probe";
        }

        var merged = MediaByteRanges.Merge(windows.Select(p => ClampToTotal(p, total))).Where(p => !p.IsEmpty).ToList();
        long targetBytes = 0;
        foreach (var window in merged)
        {
            targetBytes += window.Length;
        }

        var missing = SumMissingBytes(target, merged);
        return new MediaPrefetchPlan(stage, seconds, targetBytes, missing, merged, isEstimate, source, string.Empty);
    }

    private async Task<MediaPrefetchOutcome> PrefetchCoreAsync(
        MediaPrefetchTarget target,
        MediaPrefetchStage stage,
        int seconds,
        int maxSeconds,
        CancellationToken cancellationToken,
        IProgress<MediaPrefetchProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var started = Stopwatch.GetTimestamp();

        if (!await EnsureProbedAsync(target, cancellationToken).ConfigureAwait(false))
        {
            return NotProbed(target, stage, started);
        }

        if (!target.Registration.SupportsRange)
        {
            return Skipped(target, stage, "上游不支持 Range，跳过预取", started);
        }

        // 有 IndexRange 就试着把 SIDX 拿下来（同一目标只做一次）：有了它，秒→字节才是精确的，
        // 没有才退回按码率估算并在日志里标「估算」.
        await EnsureSegmentIndexAsync(target, cancellationToken).ConfigureAwait(false);

        var plan = stage == MediaPrefetchStage.None
            ? BuildPlanCore(target, stage, Math.Clamp(seconds, 1, 600))
            : BuildPlan(target, stage, maxSeconds);

        return await RunPlanAsync(target, plan, started, cancellationToken, progress).ConfigureAwait(false);
    }

    private async Task<MediaPrefetchOutcome> RunPlanAsync(
        MediaPrefetchTarget target,
        MediaPrefetchPlan plan,
        long started,
        CancellationToken cancellationToken,
        IProgress<MediaPrefetchProgress>? progress)
    {
        if (plan.MissingBytes <= 0)
        {
            target.CompletedStageSeconds = Math.Max(target.CompletedStageSeconds, plan.StageSeconds);
            Interlocked.Increment(ref _completedStages);
            var cachedOnly = new MediaPrefetchOutcome(
                true, false, false, plan.Stage, plan.TargetBytes, 0, plan.TargetBytes, Elapsed(started), plan.IsEstimate, "已全部命中");
            LogOutcome(target, cachedOnly, plan);
            return cachedOnly;
        }

        if (!TryReserveBudget(target, plan.MissingBytes))
        {
            var limited = new MediaPrefetchOutcome(
                false, true, false, plan.Stage, plan.TargetBytes, 0, plan.TargetBytes - plan.MissingBytes, Elapsed(started), plan.IsEstimate, "预算不足，跳过该阶段");
            LogOutcome(target, limited, plan);
            return limited;
        }

        long received = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        target.LastActivityTicks = Environment.TickCount64;
        try
        {
            foreach (var window in plan.Windows)
            {
                linked.Token.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(linked.Token).ConfigureAwait(false);
                foreach (var gap in target.Registration.Lease.GetMissingRanges(window))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var aligned = AlignWindow(gap, _proxy.Cache.BlockSize, target.Registration.Lease.Length ?? long.MaxValue);
                    if (aligned.IsEmpty)
                    {
                        continue;
                    }

                    received += await FetchRangeAsync(target, plan, aligned, linked.Token).ConfigureAwait(false);
                    progress?.Report(new MediaPrefetchProgress(plan.Stage, plan.StageSeconds, plan.TargetBytes, received, plan.TargetBytes - plan.MissingBytes + received, plan.IsEstimate));
                }
            }

            target.CompletedStageSeconds = Math.Max(target.CompletedStageSeconds, plan.StageSeconds);
            Interlocked.Increment(ref _completedStages);
            var outcome = new MediaPrefetchOutcome(
                true,
                false,
                false,
                plan.Stage,
                plan.TargetBytes,
                received,
                Math.Max(0, plan.TargetBytes - plan.MissingBytes),
                Elapsed(started),
                plan.IsEstimate,
                plan.Note);
            LogOutcome(target, outcome, plan);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelledStages);
            var outcome = new MediaPrefetchOutcome(
                false,
                false,
                true,
                plan.Stage,
                plan.TargetBytes,
                received,
                Math.Max(0, plan.TargetBytes - plan.MissingBytes),
                Elapsed(started),
                plan.IsEstimate,
                "已取消");
            LogMessage(
                MediaProxyLogLevel.Information,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"预取：bvid={target.Bvid} 阶段={(plan.Stage == MediaPrefetchStage.None ? plan.StageSeconds + "s" : plan.Stage.ToString())} 取消（已得 {received / 1024}KiB）"));
            return outcome;
        }
        finally
        {
            target.LastActivityTicks = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 真的去拉一段（走代理地址，因此复用代理的块级 flight 合并与缓存写入）.
    /// </summary>
    private async Task<long> FetchRangeAsync(MediaPrefetchTarget target, MediaPrefetchPlan plan, MediaByteRange range, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Volatile.Read(ref _inFlight);
            if (current < Math.Max(1, _options.MaxConcurrent))
            {
                if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) == current)
                {
                    break;
                }
            }
            else
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        Interlocked.Increment(ref target.InFlight);
        try
        {
            Interlocked.Add(ref _requestedBytes, range.Length);
            Interlocked.Add(ref target.RequestedBytes, range.Length);

            using var request = new HttpRequestMessage(HttpMethod.Get, target.Registration.ProxyUrl);
            request.Headers.Range = new RangeHeaderValue(range.Start, range.EndInclusive);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            var stopwatch = Stopwatch.GetTimestamp();
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
            {
                LogMessage(
                    MediaProxyLogLevel.Warning,
                    string.Create(CultureInfo.InvariantCulture, $"预取取块失败：bvid={target.Bvid} 状态={(int)response.StatusCode}"));
                return 0;
            }

            long read;
            await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                read = 0;
                while (true)
                {
                    var count = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count <= 0)
                    {
                        break;
                    }

                    read += count;
                }
            }

            if (read > 0)
            {
                Interlocked.Add(ref _receivedBytes, read);
                Interlocked.Add(ref target.PrefetchedBytes, read);
                var elapsed = Stopwatch.GetElapsedTime(stopwatch).TotalSeconds;
                if (elapsed > 0.001)
                {
                    var bps = (long)(read * 8 / elapsed);
                    // 指数滑动平均：单次抖动不至于把后面几个阶段的换算带偏.
                    target.MeasuredBps = target.MeasuredBps is { } previous && previous > 0
                        ? (long)((previous * 0.7) + (bps * 0.3))
                        : bps;
                }
            }

            if (plan.Source is "probe" or "measured" && target.MeasuredBps is { } measured && measured > 0)
            {
                // 未知码率时用实测值补一下带宽，下一阶段就不用再「试探」了.
                target.BandwidthBps = target.BandwidthBps > 0 ? target.BandwidthBps : measured;
            }

            return read;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogMessage(MediaProxyLogLevel.Warning, string.Create(CultureInfo.InvariantCulture, $"预取取块超时：bvid={target.Bvid}"));
            return 0;
        }
        finally
        {
            Interlocked.Decrement(ref target.InFlight);
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>
    /// 预算检查：不够就按 LRU 释放最冷的（且没有在飞请求的）目标，腾不出就返回 false.
    /// 正在被租约保护的缓存不会在这里被动到 —— 那是缓存的活；这里只决定「还值不值得继续预取」.
    /// </summary>
    private bool TryReserveBudget(MediaPrefetchTarget target, long needed)
    {
        if (needed <= 0)
        {
            return true;
        }

        lock (_gate)
        {
            while (true)
            {
                long tracked = 0;
                foreach (var item in _targets)
                {
                    tracked += item.PrefetchedBytes;
                }

                if (tracked + needed <= _options.BudgetBytes)
                {
                    return true;
                }

                var victim = _targets
                    .Where(p => !ReferenceEquals(p, target) && Volatile.Read(ref p.InFlight) == 0 && p.PrefetchedBytes > 0)
                    .OrderBy(p => p.LastActivityTicks)
                    .FirstOrDefault();
                if (victim is null)
                {
                    LogMessage(
                        MediaProxyLogLevel.Warning,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"预取预算不足且没有可淘汰目标：需要 {needed / 1024}KiB，已占 {tracked / 1024}KiB，预算 {_options.BudgetBytes / 1024}KiB"));
                    return false;
                }

                RecordWasteLocked(victim);
                _targets.Remove(victim);
                _evictionCount++;
                var registration = victim.OwnsRegistration ? victim.Registration : null;
                if (registration is not null)
                {
                    try
                    {
                        registration.Dispose();
                    }
                    catch
                    {
                    }
                }

                LogMessage(
                    MediaProxyLogLevel.Debug,
                    string.Create(CultureInfo.InvariantCulture, $"预取预算淘汰：{Describe(victim)}（{victim.PrefetchedBytes / 1024}KiB）"));
            }
        }
    }

    private void RecordWasteLocked(MediaPrefetchTarget target)
    {
        var cache = _proxy.Cache.GetStatistics();
        var served = Math.Max(0, cache.HitBytes - target.HitBaseline);
        var wasted = Math.Max(0, target.PrefetchedBytes - served);
        if (wasted > 0)
        {
            Interlocked.Add(ref _wastedBytes, wasted);
        }
    }

    private async Task<bool> EnsureProbedAsync(MediaPrefetchTarget target, CancellationToken cancellationToken)
    {
        if (target.Registration.Lease.Length is > 0)
        {
            return true;
        }

        using var request = new HttpRequestMessage(HttpMethod.Head, target.Registration.ProxyUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProbeTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // 代理处理 HEAD 时自己会做一次上游长度探测，回来后租约里就有长度了.
            return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent
                && target.Registration.Lease.Length is > 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogMessage(MediaProxyLogLevel.Warning, string.Create(CultureInfo.InvariantCulture, $"预取长度探测超时：bvid={target.Bvid}"));
            return false;
        }
    }

    /// <summary>
    /// 拉 init + index 字节并解析 SIDX（同一目标只做一次）.
    /// </summary>
    private async Task EnsureSegmentIndexAsync(MediaPrefetchTarget target, CancellationToken cancellationToken)
    {
        if (target.SegmentIndex is not null || target.IndexRange is not { } indexRange)
        {
            return;
        }

        var total = target.Registration.Lease.Length ?? long.MaxValue;
        var window = ClampToTotal(indexRange, total);
        if (window.IsEmpty)
        {
            return;
        }

        var bytes = await FetchRangeBytesAsync(target, window, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return;
        }

        var parsed = MediaSegmentIndex.TryParse(bytes, out var error);
        if (parsed is null)
        {
            LogMessage(
                MediaProxyLogLevel.Information,
                string.Create(CultureInfo.InvariantCulture, $"预取：bvid={target.Bvid} 的 SIDX 解析失败（{error}），改按码率估算"));
            return;
        }

        target.SegmentIndex = parsed;
        LogMessage(
            MediaProxyLogLevel.Debug,
            string.Create(CultureInfo.InvariantCulture, $"预取：bvid={target.Bvid} SIDX 就绪（{parsed}）"));
    }

    private async Task<byte[]?> FetchRangeBytesAsync(MediaPrefetchTarget target, MediaByteRange range, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Registration.ProxyUrl);
            request.Headers.Range = new RangeHeaderValue(range.Start, range.EndInclusive);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            Interlocked.Add(ref _requestedBytes, range.Length);
            Interlocked.Add(ref target.RequestedBytes, range.Length);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream((int)Math.Min(range.Length, 1024 * 1024));
            await body.CopyToAsync(memory, timeout.Token).ConfigureAwait(false);
            var data = memory.ToArray();
            if (data.Length >= range.Length)
            {
                Interlocked.Add(ref _receivedBytes, data.Length);
                Interlocked.Add(ref target.PrefetchedBytes, data.Length);
            }

            return data;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (IsPaused)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private long SumMissingBytes(MediaPrefetchTarget target, IReadOnlyList<MediaByteRange> windows)
    {
        long missing = 0;
        foreach (var window in windows)
        {
            foreach (var gap in target.Registration.Lease.GetMissingRanges(window))
            {
                missing += gap.Length;
            }
        }

        return missing;
    }

    private static long ResolveMediaStart(MediaPrefetchTarget target)
    {
        long start = 0;
        if (target.InitializationRange is { } init)
        {
            start = Math.Max(start, init.EndExclusive);
        }

        if (target.IndexRange is { } index)
        {
            start = Math.Max(start, index.EndExclusive);
            // 有些 dash 的 IndexRange 只覆盖索引本身，媒体数据紧跟着索引盒；
            // first_offset 明确给了「索引盒结尾到第一个子段」的距离，优先用它.
            if (target.SegmentIndex is { } parsed)
            {
                start += Math.Max(0, parsed.FirstOffset);
            }
        }

        return start;
    }

    private double ResolveBytesPerSecond(MediaPrefetchTarget target)
    {
        if (target.DurationSeconds is { } duration && duration > 0 && target.Registration.Lease.Length is { } total && total > 0)
        {
            var mediaStart = ResolveMediaStart(target);
            var mediaBytes = Math.Max(0, total - mediaStart);
            return mediaBytes / duration;
        }

        var bps = target.BandwidthBps > 0 ? target.BandwidthBps : target.MeasuredBps ?? 0;
        return bps > 0 ? bps / 8.0 : 0;
    }

    private MediaByteRange ClampToTotal(MediaByteRange range, long total)
    {
        if (total == long.MaxValue)
        {
            return range;
        }

        var start = Math.Min(range.Start, total);
        var end = Math.Min(range.EndExclusive, total);
        return end > start ? MediaByteRange.FromBounds(start, end) : default;
    }

    private static long AlignDown(long value, int blockSize) => value / blockSize * blockSize;

    private static long AlignUp(long value, int blockSize) => (value + blockSize - 1) / blockSize * blockSize;

    private MediaByteRange AlignWindow(MediaByteRange range, int blockSize, long total)
    {
        if (range.IsEmpty)
        {
            return default;
        }

        var start = AlignDown(range.Start, blockSize);
        var end = AlignUp(range.EndExclusive, blockSize);
        if (total != long.MaxValue)
        {
            end = Math.Min(end, total);
        }

        return end > start ? MediaByteRange.FromBounds(start, end) : default;
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private MediaPrefetchOutcome NotProbed(MediaPrefetchTarget target, MediaPrefetchStage stage, long started)
    {
        var outcome = new MediaPrefetchOutcome(false, true, false, stage, 0, 0, 0, Elapsed(started), false, "长度探测失败，跳过预取");
        LogMessage(
            MediaProxyLogLevel.Warning,
            string.Create(CultureInfo.InvariantCulture, $"预取：bvid={target.Bvid} 长度探测失败，跳过"));
        return outcome;
    }

    private MediaPrefetchOutcome Skipped(MediaPrefetchTarget target, MediaPrefetchStage stage, string note, long started)
    {
        var outcome = new MediaPrefetchOutcome(false, true, false, stage, 0, 0, 0, Elapsed(started), false, note);
        LogMessage(MediaProxyLogLevel.Information, string.Create(CultureInfo.InvariantCulture, $"预取：bvid={target.Bvid} {note}"));
        return outcome;
    }

    /// <summary>
    /// 一行诊断日志：<c>预取：bvid=… 阶段=… 目标=N MiB 实得=M MiB 用时=Tms 命中率=…</c>.
    /// </summary>
    private void LogOutcome(MediaPrefetchTarget target, MediaPrefetchOutcome outcome, MediaPrefetchPlan plan)
    {
        var stageText = outcome.Stage == MediaPrefetchStage.None
            ? plan.StageSeconds.ToString(CultureInfo.InvariantCulture) + "s"
            : outcome.Stage.ToString();
        LogMessage(
            MediaProxyLogLevel.Information,
            string.Create(
                CultureInfo.InvariantCulture,
                $"预取：bvid={target.Bvid} 阶段={stageText} 目标={outcome.TargetBytes / 1024.0 / 1024.0:F1}MiB 实得={outcome.ReceivedBytes / 1024.0 / 1024.0:F1}MiB 用时={outcome.ElapsedMilliseconds}ms 命中率={outcome.HitRate * 100:F0}%{(outcome.IsEstimate ? " [估算]" : string.Empty)} 依据={plan.Source}/{plan.StageSeconds}s{DescribeNote(plan.Note)}"));
    }

    private static string DescribeNote(string note)
        => string.IsNullOrEmpty(note) ? string.Empty : "，备注=" + note;

    private static string Describe(MediaPrefetchTarget target)
        => string.Create(CultureInfo.InvariantCulture, $"{target.DisplayName} [{target.Identity.ToDiagnosticString()}]");

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
}
