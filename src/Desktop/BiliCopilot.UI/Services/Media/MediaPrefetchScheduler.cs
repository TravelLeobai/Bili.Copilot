// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 预取调度器：把「页面看到的卡片」翻译成「现在该给谁预取多少字节」。
/// <para>
/// 它负责策略，真正搬字节的是 <see cref="MediaPrefetchService"/>：
/// <list type="bullet">
/// <item>视口哨兵每次滚动都调 <see cref="UpdateCandidates"/>；</item>
/// <item>内部 500ms 防抖（<see cref="MediaPrefetchPriorityGate"/>），快速滚动期间不发请求；</item>
/// <item>排序：悬停 / 焦点 / 刚点开 &gt; 可见面积 &gt; 停留时间 &gt; 下一屏 &gt; 刚滚出（保留 30s）；</item>
/// <item>页面关闭调 <see cref="DetachViewport"/>：取消该页全部预取并释放登记；</item>
/// <item>设置项（秒数 / 并发 / 预算 / 保留期 / 计量网络 / 播放中暂停）每次调度现读，改完下一次滚动就生效。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class MediaPrefetchScheduler : IDisposable
{
    /// <summary>
    /// 一次最多同时预取几张卡（再多预算和带宽都摊薄，收益反而下降）.
    /// </summary>
    public const int MaxCandidatesPerRun = 6;

    private readonly ILogger<MediaPrefetchScheduler> _logger;
    private readonly MediaPreResolver _resolver;
    private readonly object _gate = new();
    private readonly Dictionary<string, OwnerState> _owners = new(StringComparer.Ordinal);

    private MediaPrefetchService? _service;
    private bool _proxyDisabledLogged;
    private bool _unlockSkipLogged;
    private bool _meteredSkipLogged;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaPrefetchScheduler"/> class.
    /// </summary>
    /// <param name="resolver">预解析链路.</param>
    /// <param name="logger">日志.</param>
    public MediaPrefetchScheduler(MediaPreResolver resolver, ILogger<MediaPrefetchScheduler> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// 预取是否真的会工作：设置里的「预取」开关 + 「媒体字节缓存代理」开关都得开.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                return SettingsToolkit.ReadLocalSetting(SettingNames.PrefetchEnabled, false) && MediaBufferProxyBridge.IsEnabled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 正在使用的预取服务（E 做「按时间点空降」时直接拿它）.
    /// </summary>
    public MediaPrefetchService? Service => _service;

    /// <summary>
    /// 视口更新：把当前可见 / 悬停 / 下一屏的卡片交给调度器（每次滚动都调，内部防抖）.
    /// </summary>
    /// <param name="ownerKey">页面级归属键.</param>
    /// <param name="candidates">候选卡片.</param>
    public void UpdateCandidates(string ownerKey, IReadOnlyList<MediaPrefetchCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerKey);
        ArgumentNullException.ThrowIfNull(candidates);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        OwnerState owner;
        lock (_gate)
        {
            if (!_owners.TryGetValue(ownerKey, out owner!))
            {
                owner = new OwnerState();
                owner.Gate.Stabilized += list => OnStabilized(ownerKey, list);
                _owners[ownerKey] = owner;
            }
        }

        owner.Gate.Submit(candidates);
    }

    /// <summary>
    /// 页面关闭 / 切走：取消该页所有预取任务并释放登记.
    /// </summary>
    /// <param name="ownerKey">页面级归属键.</param>
    public void DetachViewport(string ownerKey)
    {
        if (string.IsNullOrEmpty(ownerKey))
        {
            return;
        }

        OwnerState? owner;
        lock (_gate)
        {
            if (!_owners.TryGetValue(ownerKey, out owner))
            {
                return;
            }

            _owners.Remove(ownerKey);
        }

        try
        {
            _ = owner.Cancellation?.CancelAsync();
            owner.Cancellation?.Dispose();
            owner.Cancellation = null;
        }
        catch (Exception ex)
        {
            // 取消/释放阶段的异常忽略：这里只做清理，不能把删除流程打断.
            _logger.LogDebug(ex, "预取：取消 owner 时抛异常，继续清理");
        }

        owner.Gate.Dispose();
        var released = _service?.ReleaseOwner(ownerKey) ?? 0;
        _logger.LogInformation("预取：页面关闭，取消并释放 owner={Owner}（目标 {Count} 条）", ownerKey, released);
    }

    /// <summary>
    /// 播放开始：把「点击时已有多少缓存」记进日志（这是预取到底有没有用的直接证据）.
    /// </summary>
    /// <param name="videoId">视频 id.</param>
    public void NotifyPlaybackStarting(string? videoId)
    {
        if (string.IsNullOrEmpty(videoId) || !IsEnabled)
        {
            return;
        }

        var service = _service;
        if (service is null)
        {
            return;
        }

        try
        {
            long bytes = 0;
            foreach (var target in service.Targets)
            {
                if (string.Equals(target.Bvid, videoId, StringComparison.Ordinal))
                {
                    bytes += target.GetCachedBytes();
                }
            }

            _logger.LogInformation(
                "预取：点击 {Bvid} 时已有 {Bytes} 字节缓存（{MiB} MiB）",
                videoId,
                bytes,
                Math.Round(bytes / 1024.0 / 1024.0, 2));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "预取：读取点击时缓存字节失败");
        }
    }

    /// <summary>
    /// 统计快照（F 验收 / 诊断面板用）.
    /// </summary>
    /// <returns>统计；预取没启用时返回 null.</returns>
    public MediaPrefetchStatistics? GetStatistics()
        => _service?.GetStatistics();

    /// <summary>
    /// 状态摘要（一行，诊断用）.
    /// </summary>
    /// <returns>摘要文本.</returns>
    public string GetStatusText()
    {
        var statistics = GetStatistics();
        return statistics is { } value
            ? value.ToString()
            : "预取未启用（需要同时打开「预取」与「媒体字节缓存代理」）";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var ownerKey in _owners.Keys.ToArray())
        {
            DetachViewport(ownerKey);
        }

        _service?.Dispose();
        _service = null;
    }

    private static IEnumerable<(MediaPrefetchStage Stage, int Seconds)> BuildStages(int maxSeconds)
    {
        var previous = 0;
        foreach (var stage in new[] { MediaPrefetchStage.Immediate, MediaPrefetchStage.Short, MediaPrefetchStage.Extended })
        {
            var seconds = Math.Min((int)stage, maxSeconds);
            if (seconds <= previous)
            {
                continue;
            }

            previous = seconds;
            yield return (stage, seconds);
        }
    }

    private static int ClampSeconds(int value) => Math.Clamp(value <= 0 ? 5 : value, 1, 60);

    private static bool ReadBool(SettingNames name, bool fallback)
    {
        try
        {
            return SettingsToolkit.ReadLocalSetting(name, fallback);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static int ReadInt(SettingNames name, int fallback)
    {
        try
        {
            return SettingsToolkit.ReadLocalSetting(name, fallback);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>
    /// 计量网络（流量敏感）判断：移动网络 / 计费热点都算.
    /// </summary>
    private static bool IsMeteredNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return false;
            }

            var cost = profile.GetConnectionCost();
            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnStabilized(string ownerKey, IReadOnlyList<MediaPrefetchCandidate> candidates)
    {
        _ = EvaluateAsync(ownerKey, candidates);
    }

    private async Task EvaluateAsync(string ownerKey, IReadOnlyList<MediaPrefetchCandidate> candidates)
    {
        OwnerState owner;
        CancellationToken token;
        long generation;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_owners.TryGetValue(ownerKey, out var found))
            {
                return;
            }

            owner = found;
            try
            {
                _ = owner.Cancellation?.CancelAsync();
                owner.Cancellation?.Dispose();
            }
            catch (Exception ex)
            {
                // 旧令牌源的释放失败不影响后面重建.
                _logger.LogDebug(ex, "预取：重建取消令牌时抛异常，继续");
            }

            owner.Cancellation = new CancellationTokenSource();
            token = owner.Cancellation.Token;
            generation = ++owner.Generation;
        }

        try
        {
            if (!IsEnabled)
            {
                LogProxyDisabledOnce();
                return;
            }

            if (QualityUnlockService.IsEnabled)
            {
                if (!_unlockSkipLogged)
                {
                    _unlockSkipLogged = true;
                    _logger.LogInformation("预取：画质解锁开启时不做预取（两次解析可能落在不同档位，缓存对不上）");
                }

                return;
            }

            if (ReadBool(SettingNames.PrefetchPauseDuringPlayback, true) && IsPlaybackActive())
            {
                _logger.LogDebug("预取：正在播放，暂停新预取");
                return;
            }

            if (IsMeteredNetwork() && !ReadBool(SettingNames.PrefetchOnMeteredNetwork, false))
            {
                if (!_meteredSkipLogged)
                {
                    _meteredSkipLogged = true;
                    _logger.LogInformation("预取：当前是计量网络（移动网络 / 计费热点），按设置不预取");
                }

                return;
            }

            _meteredSkipLogged = false;
            var service = EnsureService(out var reason);
            if (service is null)
            {
                if (!_proxyDisabledLogged)
                {
                    _proxyDisabledLogged = true;
                    _logger.LogWarning("预取：{Reason}", reason);
                }

                return;
            }

            _proxyDisabledLogged = false;
            ApplyLimits(service);

            var maxSeconds = ClampSeconds(ReadInt(SettingNames.PrefetchSeconds, 5));
            var retention = TimeSpan.FromSeconds(Math.Clamp(ReadInt(SettingNames.PrefetchRetentionSeconds, 30), 5, 600));
            var wanted = candidates.Take(MaxCandidatesPerRun).ToList();

            foreach (var candidate in wanted)
            {
                if (generation != owner.Generation)
                {
                    return;
                }

                token.ThrowIfCancellationRequested();
                var entry = GetOrCreateEntry(owner, candidate);

                if (entry.VideoTarget is null)
                {
                    await ResolveAndAttachAsync(entry, ownerKey, service, token).ConfigureAwait(false);
                }

                if (entry.VideoTarget is null)
                {
                    continue;
                }

                foreach (var (stage, seconds) in BuildStages(maxSeconds))
                {
                    if (entry.CompletedSeconds >= seconds)
                    {
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    var outcome = await service
                        .PrefetchAsync(entry.VideoTarget, stage, maxSeconds, token)
                        .ConfigureAwait(false);
                    if (outcome.Cancelled)
                    {
                        return;
                    }

                    if (entry.AudioTarget is not null)
                    {
                        await service.PrefetchAsync(entry.AudioTarget, stage, maxSeconds, token).ConfigureAwait(false);
                    }

                    entry.CompletedSeconds = Math.Max(entry.CompletedSeconds, seconds);
                }
            }

            SweepStaleEntries(owner, wanted, retention, service);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "预取：调度异常（不影响播放）");
        }
    }

    private Entry GetOrCreateEntry(OwnerState owner, MediaPrefetchCandidate candidate)
    {
        lock (_gate)
        {
            if (!owner.Entries.TryGetValue(candidate.VideoId, out var entry))
            {
                entry = new Entry();
                owner.Entries[candidate.VideoId] = entry;
            }

            entry.Candidate = candidate;
            entry.LastVisibleTicks = Environment.TickCount64;
            return entry;
        }
    }

    private async Task ResolveAndAttachAsync(Entry entry, string ownerKey, MediaPrefetchService service, CancellationToken token)
    {
        var candidate = entry.Candidate;
        if (candidate.IsPgc)
        {
            // 番剧走另一条解析链（PgcMediaSourceResolver），口径不同，先不预取，避免白拉.
            return;
        }

        if (entry.Resolving)
        {
            return;
        }

        entry.Resolving = true;
        try
        {
            var result = await _resolver.ResolveAsync(candidate.VideoId, candidate.Title, ownerKey, token).ConfigureAwait(false);
            if (result?.VideoSource is null)
            {
                return;
            }

            entry.VideoTarget = service.RegisterTarget(result.VideoSource, result.VideoInfo);
            if (result.AudioSource is not null)
            {
                entry.AudioTarget = service.RegisterTarget(result.AudioSource, result.AudioInfo);
            }

            _logger.LogDebug(
                "预取：目标登记 {VideoId}（qn={Quality}，{HasAudio}）",
                candidate.VideoId,
                result.Quality,
                entry.AudioTarget is not null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "预取：解析 {VideoId} 失败，跳过该卡片", candidate.VideoId);
        }
        finally
        {
            entry.Resolving = false;
        }
    }

    /// <summary>
    /// 清理「已经不在视口里、且超过保留期」的目标：取消并释放（远离的卡片不再占缓存）.
    /// </summary>
    private void SweepStaleEntries(OwnerState owner, IReadOnlyList<MediaPrefetchCandidate> wanted, TimeSpan retention, MediaPrefetchService service)
    {
        var visible = new HashSet<string>(wanted.Select(p => p.VideoId), StringComparer.Ordinal);
        List<Entry> stale;
        lock (_gate)
        {
            stale = owner.Entries
                .Where(p => !visible.Contains(p.Key) && Environment.TickCount64 - p.Value.LastVisibleTicks >= retention.TotalMilliseconds)
                .Select(p => p.Value)
                .ToList();
            foreach (var entry in stale)
            {
                owner.Entries.Remove(entry.Candidate.VideoId);
            }
        }

        foreach (var entry in stale)
        {
            if (entry.VideoTarget is not null)
            {
                service.ReleaseTarget(entry.VideoTarget);
            }

            if (entry.AudioTarget is not null)
            {
                service.ReleaseTarget(entry.AudioTarget);
            }

            _logger.LogDebug(
                "预取：脱离视口超过 {Retention}s，释放 {VideoId}",
                retention.TotalSeconds,
                entry.Candidate.VideoId);
        }
    }

    private MediaPrefetchService? EnsureService(out string reason)
    {
        reason = string.Empty;
        if (!MediaBufferProxyBridge.IsEnabled)
        {
            reason = "「媒体字节缓存代理」没开，预取不会生效（两个开关都需要打开）";
            return null;
        }

        var proxy = MediaBufferProxyBridge.EnsureStarted(_logger);
        if (proxy is null)
        {
            reason = "媒体字节缓存代理启动失败，预取暂不可用";
            return null;
        }

        if (_service is null)
        {
            _service = new MediaPrefetchService(proxy, new MediaPrefetchOptions())
            {
                Log = LogFromPrefetch,
            };
            var status = _service.GetStatistics();
            _logger.LogInformation(
                "预取：已就绪（预算 {Budget}MiB，并发 {Concurrent}）",
                status.BudgetBytes / 1024 / 1024,
                _service.Options.MaxConcurrent);
        }

        return _service;
    }

    private void ApplyLimits(MediaPrefetchService service)
    {
        service.Options.MaxConcurrent = Math.Clamp(ReadInt(SettingNames.PrefetchMaxConcurrent, 2), 1, 8);
        var budgetMiB = Math.Clamp(ReadInt(SettingNames.PrefetchBudgetMiB, 256), 32, 4096);
        service.Options.BudgetBytes = budgetMiB * 1024L * 1024L;
    }

    private bool IsPlaybackActive()
    {
        try
        {
            return this.Get<AppViewModel>().Players.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void LogProxyDisabledOnce()
    {
        if (_proxyDisabledLogged)
        {
            return;
        }

        _proxyDisabledLogged = true;
        _logger.LogInformation("预取：未启用（需要同时打开「预取」与「媒体字节缓存代理」）");
    }

    private void LogFromPrefetch(MediaProxyLogLevel level, string message)
    {
        switch (level)
        {
            case MediaProxyLogLevel.Warning:
                _logger.LogWarning("{Message}", message);
                break;
            case MediaProxyLogLevel.Error:
                _logger.LogError("{Message}", message);
                break;
            case MediaProxyLogLevel.Information:
                _logger.LogInformation("{Message}", message);
                break;
            default:
                _logger.LogDebug("{Message}", message);
                break;
        }
    }

    /// <summary>
    /// 一个页面的调度状态.
    /// </summary>
    private sealed class OwnerState
    {
        public MediaPrefetchPriorityGate Gate { get; } = new(TimeSpan.FromMilliseconds(500));

        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);

        public CancellationTokenSource? Cancellation { get; set; }

        public long Generation { get; set; }
    }

    /// <summary>
    /// 一张卡的调度状态.
    /// </summary>
    private sealed class Entry
    {
        public MediaPrefetchCandidate Candidate { get; set; } = new();

        public MediaPrefetchTarget? VideoTarget { get; set; }

        public MediaPrefetchTarget? AudioTarget { get; set; }

        public long LastVisibleTicks { get; set; }

        public int CompletedSeconds { get; set; }

        public bool Resolving { get; set; }
    }
}
