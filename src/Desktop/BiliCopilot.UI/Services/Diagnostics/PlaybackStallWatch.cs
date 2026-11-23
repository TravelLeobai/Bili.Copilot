// Copyright (c) Bili Copilot. All rights reserved.

#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BiliCopilot.UI.Services.Diagnostics;

/// <summary>
/// 停滞诊断的日志出口. 抽出来是为了让判定/写盘逻辑不依赖任何日志包，可以直接进单元测试.
/// </summary>
internal interface IPlaybackStallLog
{
    /// <summary>
    /// Gets 是否开了 Information 级别.
    /// </summary>
    bool IsInformationEnabled { get; }

    /// <summary>
    /// 写一条 Information.
    /// </summary>
    /// <param name="messageTemplate">Serilog 结构化模板.</param>
    /// <param name="values">模板参数.</param>
    void Information(string messageTemplate, params object?[] values);

    /// <summary>
    /// 写一条 Warning.
    /// </summary>
    /// <param name="messageTemplate">Serilog 结构化模板.</param>
    /// <param name="values">模板参数.</param>
    void Warning(string messageTemplate, params object?[] values);
}

/// <summary>
/// 播放停滞监视器（仅 Debug）.
/// <para>
/// 一个播放会话一个实例：拿不到播放器句柄，也不知道 mpv 的任何指针，只读探针发布的不可变快照，
/// 所以旧会话的监视线程不可能摸到已经释放的 mpv.
/// </para>
/// <para>
/// 生命周期：<see cref="Start"/> / <see cref="Stop"/>。停止是<b>异步</b>的 —— <see cref="Stop"/> 只置位
/// 就返回，从不 Join，不会卡 UI 线程；线程是后台线程，进程退出时由运行时回收.
/// </para>
/// </summary>
internal sealed class PlaybackStallWatch : IDisposable
{
    /// <summary>
    /// 监视线程的轮询间隔（毫秒）. 与上游一致：够密，又不至于自己变成负载.
    /// 这是采样节拍，不是用来掩盖竞态的等待.
    /// </summary>
    public const int WatchIntervalMilliseconds = 10;

    /// <summary>
    /// dump 目录名（落在 <c>LocalFolder\Diagnostics\</c> 下）.
    /// </summary>
    public const string DirectoryFolderName = "StallDumps";

    /// <summary>
    /// 心跳间隔（毫秒）.
    /// <para>
    /// 心跳的作用不是刷日志，而是让「这一场没有停滞」和「监视器根本没跑」在日志里能分开：
    /// 每一分钟一行，带上已经发布了多少次状态、发布间隔、当前位置与各计数.
    /// </para>
    /// </summary>
    public const int HeartbeatIntervalMilliseconds = 60_000;

    // 进程级：所有会话共用，换视频 / 重载播放器都不会重置名额.
    private static readonly PlaybackStallDumpBudget ProcessBudget = new(Stopwatch.Frequency);

    private readonly PlaybackStallProbe _probe;
    private readonly PlaybackStallPolicy _policy;
    private readonly PlaybackStallDumpBudget _budget;
    private readonly IPlaybackStallLog _log;
    private readonly string _directory;
    private readonly string _sessionId;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly long _heartbeatInterval;
    private volatile bool _stopped;
    private volatile bool _disposed;
    private int _dumps;
    private double _lastPublishIntervalMilliseconds;
    private long _lastHeartbeatAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStallWatch"/> class.
    /// </summary>
    /// <param name="directory">dump 目录.</param>
    /// <param name="sessionId">播放会话 id（与弹幕重建日志共用）.</param>
    /// <param name="log">日志出口.</param>
    public PlaybackStallWatch(string directory, string sessionId, IPlaybackStallLog log)
        : this(directory, sessionId, log, null, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStallWatch"/> class.
    /// 这个重载给测试用：可以注入时钟、探针、限流器，不用真等 180 秒.
    /// </summary>
    /// <param name="directory">dump 目录.</param>
    /// <param name="sessionId">播放会话 id.</param>
    /// <param name="log">日志出口.</param>
    /// <param name="probe">探针，null 则新建.</param>
    /// <param name="budget">限流器，null 则用进程级共享实例.</param>
    /// <param name="clock">取 tick 的委托，null 则用 <see cref="Stopwatch.GetTimestamp"/>.</param>
    public PlaybackStallWatch(
        string directory,
        string sessionId,
        IPlaybackStallLog log,
        PlaybackStallProbe? probe,
        PlaybackStallDumpBudget? budget,
        Func<long>? clock)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        _sessionId = sessionId ?? string.Empty;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _budget = budget ?? ProcessBudget;
        _probe = probe ?? new PlaybackStallProbe(clock ?? Stopwatch.GetTimestamp);

        // 策略和监视线程一律用真实 Stopwatch 口径：注入的时钟只服务探针与限流器（测试用），
        // 判定阈值必须和线程自己的时间戳同一口径，否则阈值没有意义.
        _policy = new PlaybackStallPolicy(_budget, Stopwatch.Frequency, Stopwatch.GetTimestamp());
        _heartbeatInterval = Stopwatch.Frequency * HeartbeatIntervalMilliseconds / 1000;
        _thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = "PlaybackStallWatch",
        };
    }

    /// <summary>
    /// Gets 线程真正退出后完成的 Task. 用的地方只 await 它，不 Join.
    /// </summary>
#pragma warning disable VSTHRD003 // 这个 TCS 属于专用后台线程，它永远不需要 UI 调度器.
    public Task Completion => _finished.Task;
#pragma warning restore VSTHRD003

    /// <summary>
    /// Gets 已写出的 dump 数.
    /// </summary>
    public int Dumps => Volatile.Read(ref _dumps);

    /// <summary>
    /// Gets dump 目录.
    /// </summary>
    public string Directory => _directory;

    /// <summary>
    /// 起监视线程.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thread.Start();
    }

    /// <summary>
    /// 请求停止. 立刻返回，不 Join、不等线程退出.
    /// </summary>
    public void Stop() => _stopped = true;

    /// <summary>
    /// 开始一次状态读取（对齐上游 BeginRead 语义）.
    /// </summary>
    public void BeginRead() => _probe.BeginRead();

    /// <summary>
    /// 结束一次状态读取.
    /// </summary>
    public void EndRead() => _probe.EndRead();

    /// <summary>
    /// 发布一次播放状态.
    /// </summary>
    /// <param name="playing">是否在播.</param>
    /// <param name="position">位置（秒）.</param>
    public void Observe(bool playing, double position)
    {
        var previous = _probe.Current;
        var advanced = Math.Abs(position - previous.Position) > 0.0001d;
        _probe.Observe(playing, position, advanced);
    }

    /// <summary>
    /// 报告会话进入稳定期（加载 / 拖动 / 缓冲 / 暂停恢复）.
    /// </summary>
    public void NotifySettling() => _policy.NotifySettling(Stopwatch.GetTimestamp());

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopped = true;
    }

    /// <summary>
    /// 清空 dump 目录（调试菜单 / 文档里的手动入口）.
    /// </summary>
    /// <returns>删掉的文件数.</returns>
    public int Cleanup() => PlaybackStallDump.Cleanup(_directory);

    private void Watch()
    {
        try
        {
            var previousWakeAt = Stopwatch.GetTimestamp();
            _lastHeartbeatAt = previousWakeAt;
            _policy.NotifySettling(previousWakeAt);
            while (!_stopped)
            {
                Thread.Sleep(WatchIntervalMilliseconds);

                if (_stopped)
                {
                    break;
                }

                var timestamp = Stopwatch.GetTimestamp();
                var snapshot = _probe.Current;
                var decision = _policy.Observe(timestamp, previousWakeAt, snapshot, PlaybackStallGcCounts.Read());
                _lastPublishIntervalMilliseconds = snapshot.LastPublishAt == 0
                    ? 0
                    : (timestamp - snapshot.LastPublishAt) * 1000d / Stopwatch.Frequency;
                previousWakeAt = timestamp;

                if (timestamp - _lastHeartbeatAt >= _heartbeatInterval)
                {
                    _lastHeartbeatAt = timestamp;
                    LogHeartbeat(snapshot, timestamp);
                }

                if (decision == PlaybackStallDecision.None)
                {
                    continue;
                }

                if (decision != PlaybackStallDecision.Dump)
                {
                    LogSkip(decision, snapshot, timestamp);
                    continue;
                }

                // 判完到写之前再确认一次：停止请求已经下来的话不再新开 dump.
                if (_stopped)
                {
                    break;
                }

                WriteDump(snapshot, timestamp);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(
                "STALL | watcher-crashed session={Session} error={Error}",
                _sessionId,
                ex.GetType().Name);
        }
        finally
        {
            LogSummary();
            _finished.TrySetResult();
        }
    }

    private void WriteDump(PlaybackStallSnapshot snapshot, long timestamp)
    {
        var kind = _policy.LastKind;
        var context = BuildContext(snapshot, timestamp, kind);
        if (!PlaybackStallDump.TryWrite(_directory, DateTime.UtcNow, context, out var result, out var error))
        {
            _log.Warning(
                "STALL | dump-failed session={Session} type={Type} observed-ms={ObservedMs} playing={Playing} pos={Position} gc0={Gc0} gc1={Gc1} gc2={Gc2} error={Error}",
                _sessionId,
                NameOf(kind),
                _policy.LastObservedMilliseconds.ToString("0.0", CultureInfo.InvariantCulture),
                snapshot.Playing,
                snapshot.Position.ToString("0.###", CultureInfo.InvariantCulture),
                snapshot.Collections.Gen0,
                snapshot.Collections.Gen1,
                snapshot.Collections.Gen2,
                error);
            return;
        }

        Interlocked.Increment(ref _dumps);
        _log.Information(
            "STALL | dump session={Session} type={Type} observed-ms={ObservedMs} read-ms={ReadMs} publish-ms={PublishMs} playing={Playing} pos={Position} gc0={Gc0} gc1={Gc1} gc2={Gc2} attempts={Attempts} remaining={Remaining} file={File} bytes={Bytes} write-ms={WriteMs} tick={Tick} utc={Utc}",
            _sessionId,
            NameOf(kind),
            _policy.LastObservedMilliseconds.ToString("0.0", CultureInfo.InvariantCulture),
            snapshot.ReadId == 0 ? "0.0" : Milliseconds(timestamp - snapshot.ReadStartedAt),
            _lastPublishIntervalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture),
            snapshot.Playing,
            snapshot.Position.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.Collections.Gen0,
            snapshot.Collections.Gen1,
            snapshot.Collections.Gen2,
            _budget.Attempts,
            _budget.Remaining,
            result.FileName,
            result.Bytes,
            result.WriteMilliseconds.ToString("0.0", CultureInfo.InvariantCulture),
            timestamp,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    private void LogSkip(PlaybackStallDecision decision, PlaybackStallSnapshot snapshot, long timestamp)
    {
        if (!_log.IsInformationEnabled)
        {
            return;
        }

        var reason = decision switch
        {
            PlaybackStallDecision.SkippedGc => "gc-or-wake-gap",
            PlaybackStallDecision.SkippedSettling => "settling-or-not-playing",
            PlaybackStallDecision.SkippedRate => "rate-limited",
            _ => "unknown",
        };

        _log.Information(
            "STALL | skipped session={Session} type={Type} reason={Reason} observed-ms={ObservedMs} playing={Playing} pos={Position} gc0={Gc0} attempts={Attempts} remaining={Remaining} tick={Tick}",
            _sessionId,
            NameOf(_policy.LastKind),
            reason,
            _policy.LastObservedMilliseconds.ToString("0.0", CultureInfo.InvariantCulture),
            snapshot.Playing,
            snapshot.Position.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.Collections.Gen0,
            _budget.Attempts,
            _budget.Remaining,
            timestamp);
    }

    private void LogHeartbeat(PlaybackStallSnapshot snapshot, long timestamp)
    {
        if (!_log.IsInformationEnabled)
        {
            return;
        }

        _log.Information(
            "STALL | heartbeat session={Session} publishes={Publishes} publish-gap-ms={PublishGapMs} playing={Playing} pos={Position} stalls={Stalls} dumps={Dumps} skipped-gc={SkippedGc} skipped-settling={SkippedSettling} skipped-rate={SkippedRate} attempts={Attempts} remaining={Remaining} tick={Tick}",
            _sessionId,
            snapshot.PublishCount,
            _lastPublishIntervalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture),
            snapshot.Playing,
            snapshot.Position.ToString("0.###", CultureInfo.InvariantCulture),
            _policy.Stalls,
            _dumps,
            _policy.SkippedGc,
            _policy.SkippedSettling,
            _policy.SkippedRate,
            _budget.Attempts,
            _budget.Remaining,
            timestamp);
    }

    private void LogSummary()
    {
        if (!_log.IsInformationEnabled)
        {
            return;
        }

        _log.Information(
            "STALL | watch-stopped session={Session} stalls={Stalls} dumps={Dumps} skipped-gc={SkippedGc} skipped-settling={SkippedSettling} skipped-rate={SkippedRate} attempts={Attempts} remaining={Remaining}",
            _sessionId,
            _policy.Stalls,
            _dumps,
            _policy.SkippedGc,
            _policy.SkippedSettling,
            _policy.SkippedRate,
            _budget.Attempts,
            _budget.Remaining);
    }

    private string BuildContext(PlaybackStallSnapshot snapshot, long timestamp, PlaybackStallKind kind)
    {
        // 只写结构化事实：不写 URL、Cookie、Token.
        var builder = new StringBuilder();
        builder.Append("session=").AppendLine(_sessionId);
        builder.Append("stallType=").AppendLine(NameOf(kind));
        builder.Append("observed-ms=").AppendLine(_policy.LastObservedMilliseconds.ToString("0.0", CultureInfo.InvariantCulture));
        builder.Append("playing=").AppendLine(snapshot.Playing ? "true" : "false");
        builder.Append("position=").AppendLine(snapshot.Position.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append("read-ms=").AppendLine(snapshot.ReadId == 0 ? "0.0" : Milliseconds(timestamp - snapshot.ReadStartedAt));
        builder.Append("advance-ms=").AppendLine(Milliseconds(timestamp - snapshot.AdvanceAt));
        builder.Append("publish-ms=").AppendLine(_lastPublishIntervalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture));
        builder.Append("gc=").Append(snapshot.Collections.Gen0).Append('/').Append(snapshot.Collections.Gen1).Append('/').Append(snapshot.Collections.Gen2.ToString(CultureInfo.InvariantCulture)).AppendLine();
        builder.Append("tick=").AppendLine(timestamp.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("privacy=该文件含本进程内存明文（可能包括 Cookie / Token / 鉴权 URL），不要上传、不要附到 Issue、不要进公开仓库。");
        return builder.ToString();
    }

    private static string Milliseconds(long ticks)
        => (ticks * 1000d / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture);

    private static string NameOf(PlaybackStallKind kind) => kind switch
    {
        PlaybackStallKind.ProgressRead => "progress-read",
        PlaybackStallKind.ProgressGap => "progress-gap",
        _ => "none",
    };
}
#endif
