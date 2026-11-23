// Copyright (c) Bili Copilot. All rights reserved.

#if DEBUG
using System.Diagnostics;

namespace BiliCopilot.UI.Services.Diagnostics;

/// <summary>
/// dump 限流器（进程级）.
/// <para>
/// 所有播放会话共用一个实例：换视频、重载播放器都不会重置名额。一次失败的尝试同样占掉一个名额，
/// 诊断不会重试同一次停滞 —— 这样「反复失败写不出 dump」也不会变成死循环.
/// </para>
/// <para>
/// 时间口径是 <see cref="Stopwatch"/> tick，不是墙钟：改系统时间不会放开限流.
/// </para>
/// </summary>
internal sealed class PlaybackStallDumpBudget
{
    /// <summary>
    /// 两次尝试之间的最短间隔（秒）.
    /// </summary>
    public const int DefaultMinimumIntervalSeconds = 180;

    /// <summary>
    /// 一个进程里最多尝试写几次 dump.
    /// </summary>
    public const int DefaultMaximumAttempts = 5;

    private readonly object _sync = new();
    private readonly long _minimumInterval;
    private readonly Func<long> _clock;
    private readonly int _maximumAttempts;
    private long _lastAttemptAt;
    private int _attempts;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStallDumpBudget"/> class.
    /// </summary>
    /// <param name="frequency">tick 频率，正常用 <see cref="Stopwatch.Frequency"/>.</param>
    /// <param name="clock">取当前 tick 的委托，默认 <see cref="Stopwatch.GetTimestamp"/>；测试注入假时钟.</param>
    /// <param name="maximumAttempts">最多尝试次数.</param>
    /// <param name="minimumIntervalSeconds">最短间隔（秒）.</param>
    public PlaybackStallDumpBudget(
        long frequency,
        Func<long>? clock = null,
        int maximumAttempts = DefaultMaximumAttempts,
        int minimumIntervalSeconds = DefaultMinimumIntervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _minimumInterval = frequency * minimumIntervalSeconds;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _maximumAttempts = maximumAttempts;
    }

    /// <summary>
    /// Gets 已经占用的尝试次数.
    /// </summary>
    public int Attempts
    {
        get
        {
            lock (_sync)
            {
                return _attempts;
            }
        }
    }

    /// <summary>
    /// Gets 还剩几次名额.
    /// </summary>
    public int Remaining
    {
        get
        {
            lock (_sync)
            {
                return Math.Max(0, _maximumAttempts - _attempts);
            }
        }
    }

    /// <summary>
    /// 申请一个名额.
    /// </summary>
    /// <param name="timestamp">当前 tick.</param>
    /// <returns>拿到名额返回 <see langword="true"/>.</returns>
    public bool TryReserve(long timestamp)
    {
        lock (_sync)
        {
            if (_attempts >= _maximumAttempts)
            {
                return false;
            }

            if (_attempts != 0 && timestamp - _lastAttemptAt < _minimumInterval)
            {
                return false;
            }

            _lastAttemptAt = timestamp;
            _attempts++;
            return true;
        }
    }

    /// <summary>
    /// 取当前时刻（同一时钟口径，便于调用方拼日志）.
    /// </summary>
    /// <returns>tick.</returns>
    public long Now() => _clock();
}
#endif
