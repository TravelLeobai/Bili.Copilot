// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services.Mpv;

/// <summary>一段可以直接跳过去的已缓存区间（秒）.</summary>
/// <param name="Start">起点.</param>
/// <param name="End">终点.</param>
public readonly record struct MpvSeekableRangeSnapshot(double Start, double End);

/// <summary>
/// 某一刻的缓冲实况.
/// <para>
/// 注意：这里全部是<b>实测值</b>，不是设置里的上限 ——
/// 面板上显示「配置 300MiB、实际只有 12MiB」才是正确状态，不能拿配置值顶替.
/// 读不到的项保持 null，由展示层写「不支持」，不许填 0 或配置值假装成功.
/// </para>
/// </summary>
public sealed record MpvCacheSnapshot
{
    /// <summary>是否已经收到过 demuxer-cache-state 事件（没收到就说明没有真实数据可用）.</summary>
    public bool HasEventData { get; init; }

    /// <summary>前向缓存字节数（fw-bytes）.</summary>
    public long ForwardBytes { get; init; }

    /// <summary>磁盘缓存字节数（file-cache-bytes）.</summary>
    public long FileCacheBytes { get; init; }

    /// <summary>是否已经缓到了流开头.</summary>
    public bool BofCached { get; init; }

    /// <summary>是否已经缓到了流结尾（和 <see cref="BofCached"/> 同时为 true 表示整段都缓完了）.</summary>
    public bool EofCached { get; init; }

    /// <summary>可直接跳转的已缓存区间.</summary>
    public IReadOnlyList<MpvSeekableRangeSnapshot> SeekableRanges { get; init; } = Array.Empty<MpvSeekableRangeSnapshot>();

    /// <summary>已缓冲的时长（秒）；null = 读不到（属性当前不可用或绑定不支持）.</summary>
    public double? CacheDurationSeconds { get; init; }

    /// <summary>缓冲进度 0~100；null = 读不到. 100 表示不在缓冲.</summary>
    public int? BufferingPercent { get; init; }

    /// <summary>是否正在缓冲（有真实进度且小于 100 才算，读不到就当不知道，不当成缓冲中）.</summary>
    public bool IsBuffering => BufferingPercent is >= 0 and < 100;
}

/// <summary>
/// 缓冲实况读取器.
/// <para>
/// 数据有两个来源，分工固定：<br/>
/// 1. 前向/磁盘字节、可跳区间、bof/eof —— 来自内核解析好的 <c>MpvClient.CacheStateChanged</c> 事件
///    （这里只把数值记下来，事件线程写、UI 线程读，用的是引用赋值，不加锁）；<br/>
/// 2. <c>demuxer-cache-duration</c> / <c>cache-buffering-state</c> —— 读的时候现查属性桥，
///    读不到就留 null（面板写「不支持」）.
/// </para>
/// <para>
/// 调用频率由调用方控制（统计浮层是 1Hz），这个类自己不起定时器.
/// </para>
/// </summary>
public sealed class MpvCacheStateReader
{
    private CacheEventSnapshot? _last;

    /// <summary>
    /// 记一条 demuxer-cache-state 事件（在 mpv 事件线程上调用，只做一次引用赋值）.
    /// </summary>
    /// <param name="forwardBytes">fw-bytes.</param>
    /// <param name="fileCacheBytes">file-cache-bytes.</param>
    /// <param name="bofCached">bof-cached.</param>
    /// <param name="eofCached">eof-cached.</param>
    /// <param name="seekableRanges">seekable-ranges.</param>
    public void RecordEvent(
        long forwardBytes,
        long fileCacheBytes,
        bool bofCached,
        bool eofCached,
        IReadOnlyList<MpvSeekableRangeSnapshot> seekableRanges)
        => Volatile.Write(
            ref _last,
            new CacheEventSnapshot(forwardBytes, fileCacheBytes, bofCached, eofCached, seekableRanges ?? Array.Empty<MpvSeekableRangeSnapshot>()));

    /// <summary>
    /// 清掉上一次播放留下的数据（换集时调，避免面板显示上一条的缓冲实况）.
    /// </summary>
    public void Reset() => Volatile.Write(ref _last, null);

    /// <summary>
    /// 读一份当前缓冲实况. 每次调用都是独立快照，失败项为 null.
    /// </summary>
    /// <param name="bridge">
    /// 属性桥，用来现查 demuxer-cache-duration / cache-buffering-state；
    /// 为空表示这两项没有通道可读，结果里对应字段为 null（展示层写「不支持」）.
    /// </param>
    /// <returns>缓冲实况.</returns>
    public MpvCacheSnapshot Read(IMpvPropertyBridge? bridge = null)
    {
        var last = Volatile.Read(ref _last);

        double? duration = null;
        int? buffering = null;
        if (bridge is not null)
        {
            if (bridge.TryGetDouble(MpvCachePropertyNames.DemuxerCacheDuration, out var seconds) && seconds >= 0)
            {
                duration = seconds;
            }

            if (bridge.TryGetInt64(MpvCachePropertyNames.CacheBufferingState, out var percent) && percent is >= 0 and <= 100)
            {
                buffering = (int)percent;
            }
        }

        return new MpvCacheSnapshot
        {
            HasEventData = last is not null,
            ForwardBytes = last?.ForwardBytes ?? 0,
            FileCacheBytes = last?.FileCacheBytes ?? 0,
            BofCached = last?.BofCached ?? false,
            EofCached = last?.EofCached ?? false,
            SeekableRanges = last?.SeekableRanges ?? Array.Empty<MpvSeekableRangeSnapshot>(),
            CacheDurationSeconds = duration,
            BufferingPercent = buffering,
        };
    }

    private sealed record CacheEventSnapshot(
        long ForwardBytes,
        long FileCacheBytes,
        bool BofCached,
        bool EofCached,
        IReadOnlyList<MpvSeekableRangeSnapshot> SeekableRanges);
}
