// Copyright (c) Bili Copilot. All rights reserved.

#if DEBUG
namespace BiliCopilot.UI.Services.Diagnostics;

/// <summary>
/// 判定出来的停滞类型.
/// </summary>
internal enum PlaybackStallKind
{
    /// <summary>
    /// 没有停滞.
    /// </summary>
    None = 0,

    /// <summary>
    /// 一次状态读取 + 消费超过阈值还没收尾：通常是 UI 线程被弹幕渲染 / 布局卡住.
    /// 语义对齐上游 BiliFluent 的「同步属性读取被阻塞」.
    /// </summary>
    ProgressRead = 1,

    /// <summary>
    /// 正在播放，但播放位置超过阈值没有前进：通常是 mpv 内核不再推进，
    /// 或位置通知根本送不到。这是本仓新增的判据（本仓没有同步读取路径可复用）.
    /// </summary>
    ProgressGap = 2,
}

/// <summary>
/// 一次判定结果.
/// </summary>
internal enum PlaybackStallDecision
{
    /// <summary>
    /// 没有达到任何阈值.
    /// </summary>
    None = 0,

    /// <summary>
    /// 应当写 dump.
    /// </summary>
    Dump,

    /// <summary>
    /// 判为停滞，但期间发生了 GC（或监视线程自己被饿），样本不可信，跳过.
    /// </summary>
    SkippedGc,

    /// <summary>
    /// 判为停滞，但会话还在稳定期 / 当前不在播放（暂停、拖动、缓冲、加载），跳过.
    /// </summary>
    SkippedSettling,

    /// <summary>
    /// 判为停滞，但进程级名额或间隔挡住了，跳过.
    /// </summary>
    SkippedRate,
}

/// <summary>
/// 停滞判定策略：阈值 + 抑制条件 + 去重，不碰任何播放器对象.
/// </summary>
internal sealed class PlaybackStallPolicy
{
    /// <summary>
    /// 一次状态读取的停滞阈值（毫秒）. 取自上游 BiliFluent，语义相同：一次读取 + 消费超过它就值得抓栈.
    /// </summary>
    public const int ReadStallThresholdMilliseconds = 60;

    /// <summary>
    /// 位置停滞阈值（毫秒）.
    /// <para>
    /// 上游没有这一档（它的同步读取会自己阻塞，天然能发现 mpv 不动）。本仓的位置通知是内核推的，
    /// 内核真卡住时连通知都没有，只能靠「位置多久没前进」发现。600ms 取一个明显超出正常通知间隔、
    /// 又能在用户已经感知到画面冻住时触发的值；实际发布间隔记在日志的 publish-ms 里，便于回调校准.
    /// </para>
    /// </summary>
    public const int ProgressGapThresholdMilliseconds = 600;

    /// <summary>
    /// 稳定期（毫秒）：加载、切视频、暂停恢复、拖动之后这段时间内的停滞一律不采.
    /// </summary>
    public const int SettlingPeriodMilliseconds = 5000;

    /// <summary>
    /// 监视线程相邻两次醒来的最大间隔（毫秒）：超过它说明监视线程自己被饿过，这一轮的耗时不作数.
    /// </summary>
    public const int MaximumWakeGapMilliseconds = 30;

    private readonly PlaybackStallDumpBudget _budget;
    private readonly long _frequency;
    private readonly long _readThreshold;
    private readonly long _progressGapThreshold;
    private readonly long _settlingPeriod;
    private readonly long _maximumWakeGap;
    private long _settlingAt;
    private long _seenReadId;
    private long _seenAdvanceId;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStallPolicy"/> class.
    /// </summary>
    /// <param name="budget">进程级限流器.</param>
    /// <param name="frequency">tick 频率，正常用 <see cref="System.Diagnostics.Stopwatch.Frequency"/>.</param>
    /// <param name="startedAt">会话起点 tick，稳定期从这里算.</param>
    public PlaybackStallPolicy(PlaybackStallDumpBudget budget, long frequency, long startedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _budget = budget;
        _frequency = frequency;
        _readThreshold = frequency * ReadStallThresholdMilliseconds / 1000;
        _progressGapThreshold = frequency * ProgressGapThresholdMilliseconds / 1000;
        _settlingPeriod = frequency * SettlingPeriodMilliseconds / 1000;
        _maximumWakeGap = frequency * MaximumWakeGapMilliseconds / 1000;
        _settlingAt = startedAt;
    }

    /// <summary>
    /// Gets 判为停滞（达到阈值）的次数，含最终被抑制掉的.
    /// </summary>
    public int Stalls { get; private set; }

    /// <summary>
    /// Gets 因 GC / 监视线程被饿而跳过的次数.
    /// </summary>
    public int SkippedGc { get; private set; }

    /// <summary>
    /// Gets 因稳定期 / 不在播放而跳过的次数.
    /// </summary>
    public int SkippedSettling { get; private set; }

    /// <summary>
    /// Gets 因限流而跳过的次数.
    /// </summary>
    public int SkippedRate { get; private set; }

    /// <summary>
    /// Gets 最近一次判定出的停滞类型.
    /// </summary>
    public PlaybackStallKind LastKind { get; private set; }

    /// <summary>
    /// Gets 最近一次停滞的观测值（毫秒）：<see cref="PlaybackStallKind.ProgressRead"/> 是读取耗时，
    /// <see cref="PlaybackStallKind.ProgressGap"/> 是位置没前进的时长.
    /// </summary>
    public double LastObservedMilliseconds { get; private set; }

    /// <summary>
    /// Gets 最近一次判定时看到的快照.
    /// </summary>
    public PlaybackStallSnapshot LastSnapshot { get; private set; } = PlaybackStallSnapshot.Empty;

    /// <summary>
    /// 报告一次「会话在稳定中」：加载、切视频、暂停恢复、拖动、缓冲状态变化都调它.
    /// 只把时间戳往大推，不往回退（并发调用安全）.
    /// </summary>
    /// <param name="timestamp">当前 tick.</param>
    public void NotifySettling(long timestamp)
    {
        var previous = Volatile.Read(ref _settlingAt);
        while (timestamp > previous)
        {
            var observed = Interlocked.CompareExchange(ref _settlingAt, timestamp, previous);
            if (observed == previous)
            {
                return;
            }

            previous = observed;
        }
    }

    /// <summary>
    /// 做一次判定.
    /// </summary>
    /// <param name="timestamp">当前 tick.</param>
    /// <param name="previousWakeAt">上一轮醒来的 tick.</param>
    /// <param name="snapshot">当前快照.</param>
    /// <param name="collections">当前 GC 计数.</param>
    /// <returns>判定结果.</returns>
    public PlaybackStallDecision Observe(
        long timestamp,
        long previousWakeAt,
        PlaybackStallSnapshot snapshot,
        PlaybackStallGcCounts collections)
    {
        // 一：读取停滞（对齐上游语义）.
        if (snapshot.ReadId != 0 && snapshot.ReadId != _seenReadId)
        {
            var pending = timestamp - snapshot.ReadStartedAt;
            if (pending >= _readThreshold)
            {
                _seenReadId = snapshot.ReadId;
                Stalls++;
                LastKind = PlaybackStallKind.ProgressRead;
                LastObservedMilliseconds = ToMilliseconds(pending);
                LastSnapshot = snapshot;
                return Gate(timestamp, previousWakeAt, snapshot, collections, requirePlaying: true);
            }
        }

        // 二：位置停滞（本仓新增）.
        if (snapshot.Playing && snapshot.AdvanceId != _seenAdvanceId)
        {
            var gap = timestamp - snapshot.AdvanceAt;
            if (gap >= _progressGapThreshold)
            {
                _seenAdvanceId = snapshot.AdvanceId;
                Stalls++;
                LastKind = PlaybackStallKind.ProgressGap;
                LastObservedMilliseconds = ToMilliseconds(gap);
                LastSnapshot = snapshot;
                return Gate(timestamp, previousWakeAt, snapshot, collections, requirePlaying: false);
            }
        }

        return PlaybackStallDecision.None;
    }

    private double ToMilliseconds(long ticks) => ticks * 1000d / _frequency;

    private PlaybackStallDecision Gate(
        long timestamp,
        long previousWakeAt,
        PlaybackStallSnapshot snapshot,
        PlaybackStallGcCounts collections,
        bool requirePlaying)
    {
        // 监视线程自己刚被饿过 / 这段里发生过 GC：这段耗时不是「卡在读取上」的证据.
        if (timestamp - previousWakeAt > _maximumWakeGap
            || snapshot.ReadCollections != collections
            || snapshot.Collections != collections)
        {
            SkippedGc++;
            return PlaybackStallDecision.SkippedGc;
        }

        // 不在播放（暂停 / 拖动 / 缓冲 / 加载 / 已停）或者还在稳定期：不采.
        if ((requirePlaying && !snapshot.Playing)
            || timestamp - Volatile.Read(ref _settlingAt) < _settlingPeriod)
        {
            SkippedSettling++;
            return PlaybackStallDecision.SkippedSettling;
        }

        if (!_budget.TryReserve(timestamp))
        {
            SkippedRate++;
            return PlaybackStallDecision.SkippedRate;
        }

        return PlaybackStallDecision.Dump;
    }
}
#endif
