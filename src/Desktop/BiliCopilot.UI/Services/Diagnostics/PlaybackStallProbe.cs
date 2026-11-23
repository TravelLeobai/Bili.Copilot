// Copyright (c) Bili Copilot. All rights reserved.

#if DEBUG
using System.Diagnostics;

namespace BiliCopilot.UI.Services.Diagnostics;

/// <summary>
/// 一次 GC 计数快照. 用来判断「这段卡顿是不是 GC 造成的」.
/// </summary>
/// <param name="Gen0">Gen0 回收次数.</param>
/// <param name="Gen1">Gen1 回收次数.</param>
/// <param name="Gen2">Gen2 回收次数.</param>
internal readonly record struct PlaybackStallGcCounts(int Gen0, int Gen1, int Gen2)
{
    /// <summary>
    /// 读当前进程的 GC 计数.
    /// </summary>
    /// <returns>GC 计数快照.</returns>
    public static PlaybackStallGcCounts Read()
        => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
}

/// <summary>
/// 一次状态发布后的不可变快照. 监视线程只读它，永不碰播放器对象.
/// </summary>
/// <param name="ReadId">正在进行的「一次状态读取」编号；0 表示当前没有在飞的读取.</param>
/// <param name="ReadStartedAt">那次读取的开始时间（<see cref="Stopwatch"/> tick）.</param>
/// <param name="ReadCollections">那次读取开始时的 GC 计数.</param>
/// <param name="Playing">最近一次发布时是否处于「正在播放」.</param>
/// <param name="AdvanceId">播放位置前进过的次数.</param>
/// <param name="AdvanceAt">最后一次位置真正前进的时间（<see cref="Stopwatch"/> tick）.</param>
/// <param name="LastPublishAt">最近一次发布的时间（<see cref="Stopwatch"/> tick）.</param>
/// <param name="Collections">最近一次发布时的 GC 计数.</param>
/// <param name="Position">最近一次发布的播放位置（秒）.</param>
/// <param name="PublishCount">从探针建立到现在一共发布过多少次状态（心跳靠它证明接线还活着）.</param>
internal sealed record PlaybackStallSnapshot(
    long ReadId,
    long ReadStartedAt,
    PlaybackStallGcCounts ReadCollections,
    bool Playing,
    long AdvanceId,
    long AdvanceAt,
    long LastPublishAt,
    PlaybackStallGcCounts Collections,
    double Position,
    long PublishCount)
{
    /// <summary>
    /// 从未发布过任何状态时的空快照.
    /// </summary>
    public static PlaybackStallSnapshot Empty { get; } = new(0, 0, default, false, 0, 0, 0, default, 0d, 0);
}

/// <summary>
/// 播放停滞探针.
/// <para>
/// 本仓的 mpv 内核（Richasy.MpvKernel 0.0.2-preview2）没有暴露同步属性读取，位置与播放状态是
/// 由内核回调推进来的（<c>MpvPlayer.PropertyChanged</c>）。因此这里不新开任何 mpv 查询：探针只在
/// 「既有状态读取点」被喂数据 ——
/// </para>
/// <list type="bullet">
/// <item><description><see cref="BeginRead"/> / <see cref="EndRead"/> 包住一次状态读取 + 消费（进度通知落到弹幕渲染层）.</description></item>
/// <item><description><see cref="Observe"/> 记下位置是否前进、当前是否在播、当时的 GC 计数.</description></item>
/// </list>
/// <para>
/// 只有一个线程发布（属性变更回调），读取方（监视线程）不加锁，读到的是不可变快照。
/// </para>
/// </summary>
internal sealed class PlaybackStallProbe
{
    private readonly Func<long> _clock;
    private readonly Func<PlaybackStallGcCounts> _collections;
    private readonly object _sync = new();
    private PlaybackStallSnapshot _current;
    private long _nextReadId;
    private long _advanceId;
    private long _advanceAt;
    private long _publishCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStallProbe"/> class.
    /// </summary>
    /// <param name="clock">取当前 tick 的委托，默认 <see cref="Stopwatch.GetTimestamp"/>；测试注入假时钟.</param>
    /// <param name="collections">取 GC 计数的委托，测试注入假计数.</param>
    public PlaybackStallProbe(Func<long>? clock = null, Func<PlaybackStallGcCounts>? collections = null)
    {
        _clock = clock ?? Stopwatch.GetTimestamp;
        _collections = collections ?? PlaybackStallGcCounts.Read;
        _current = PlaybackStallSnapshot.Empty;
        var now = _clock();
        _advanceAt = now;
        _current = _current with { AdvanceAt = now, LastPublishAt = now, Collections = _collections() };
    }

    /// <summary>
    /// Gets 当前快照（无锁读）.
    /// </summary>
    public PlaybackStallSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// 开始一次状态读取：记下起点时刻与当时的 GC 计数.
    /// 读取方在 <see cref="EndRead"/> 之前一直能看到这个编号 —— 编号还在而耗时超阈值，就是一次「读取停滞」.
    /// </summary>
    public void BeginRead()
    {
        lock (_sync)
        {
            var now = _clock();
            _current = _current with
            {
                ReadId = ++_nextReadId,
                ReadStartedAt = now,
                ReadCollections = _collections(),
            };
        }
    }

    /// <summary>
    /// 结束一次状态读取.
    /// </summary>
    public void EndRead()
    {
        lock (_sync)
        {
            if (_current.ReadId == 0)
            {
                return;
            }

            _current = _current with { ReadId = 0 };
        }
    }

    /// <summary>
    /// 发布一次状态观察.
    /// </summary>
    /// <param name="playing">当前是否处于「正在播放」（缓冲 / 拖动 / 加载 / 暂停都算否）.</param>
    /// <param name="position">当前播放位置（秒）.</param>
    /// <param name="positionAdvanced">位置相对上一次观察是否真的前进了.</param>
    public void Observe(bool playing, double position, bool positionAdvanced)
    {
        lock (_sync)
        {
            var now = _clock();
            if (positionAdvanced)
            {
                _advanceId++;
                _advanceAt = now;
            }

            _current = _current with
            {
                Playing = playing,
                Position = position,
                AdvanceId = _advanceId,
                AdvanceAt = _advanceAt,
                LastPublishAt = now,
                Collections = _collections(),
                PublishCount = ++_publishCount,
            };
        }
    }
}
#endif
