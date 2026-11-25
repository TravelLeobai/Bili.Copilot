// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 媒体字节范围缓存：按「逻辑媒体身份」存字节块，供本机回环代理复用.
/// <para>
/// 结构：每个身份一个条目，条目内按固定大小块（默认 256 KiB）稀疏存 byte[]；
/// 只有「完整块」才算命中，所以部分命中天然成立（播放器要 [0, 1 MiB) 时，
/// 若只有中间某几块在，代理只会去补缺的那几块）。
/// </para>
/// <para>
/// 容量与淘汰：总字节数有上限（默认 256 MiB），超了按 LRU 淘汰「没被租约保护」的条目。
/// 租约（<see cref="MediaCacheLease"/>）代表「这个条目正被播放器读」，带租约的条目永不淘汰；
/// 如果所有条目都被租着，宁可放弃写入（返回 false）也不破坏正在播的数据。
/// </para>
/// <para>
/// 线程安全：所有公开成员可在任意线程调用；锁只保护内存结构，不做任何 IO。
/// </para>
/// </summary>
public sealed class MediaRangeCache
{
    /// <summary>
    /// 默认块大小：256 KiB.
    /// </summary>
    public const int DefaultBlockSize = 256 * 1024;

    /// <summary>
    /// 默认容量上限：256 MiB.
    /// </summary>
    public const long DefaultCapacityBytes = 256L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<MediaStreamIdentity, MediaRangeCacheEntry> _entries = [];
    private readonly long _capacityBytes;
    private readonly int _blockSize;

    private long _accessClock;
    private long _currentBytes;
    private long _evictedBytes;
    private long _hitBytes;
    private long _missBytes;
    private int _evictionCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaRangeCache"/> class.
    /// </summary>
    /// <param name="capacityBytes">容量上限（字节），默认 256 MiB. 必须大于 0.</param>
    /// <param name="blockSize">块大小（字节），默认 256 KiB. 必须大于 0.</param>
    public MediaRangeCache(long capacityBytes = DefaultCapacityBytes, int blockSize = DefaultBlockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        _capacityBytes = capacityBytes;
        _blockSize = blockSize;
    }

    /// <summary>
    /// 容量上限（字节）.
    /// </summary>
    public long CapacityBytes => _capacityBytes;

    /// <summary>
    /// 块大小（字节）.
    /// </summary>
    public int BlockSize => _blockSize;

    /// <summary>
    /// 拿一个条目租约（不存在就建一个空条目）.
    /// 租约保护该条目在持有期间不被 LRU 淘汰；用完必须 <see cref="MediaCacheLease.Dispose"/>.
    /// </summary>
    /// <param name="identity">逻辑媒体身份.</param>
    /// <returns>租约.</returns>
    public MediaCacheLease AcquireLease(MediaStreamIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            if (!_entries.TryGetValue(identity, out var entry))
            {
                entry = new MediaRangeCacheEntry(identity);
                _entries[identity] = entry;
            }

            entry.LeaseCount++;
            entry.LastAccess = ++_accessClock;
            return new MediaCacheLease(this, entry);
        }
    }

    /// <summary>
    /// 取一份统计快照.
    /// </summary>
    /// <returns>统计.</returns>
    public MediaCacheStatistics GetStatistics()
    {
        lock (_gate)
        {
            var blocks = 0;
            foreach (var entry in _entries.Values)
            {
                blocks += entry.Blocks.Count;
            }

            return new MediaCacheStatistics(
                _hitBytes,
                _missBytes,
                _evictedBytes,
                _currentBytes,
                _entries.Count,
                blocks,
                _evictionCount,
                _capacityBytes,
                _blockSize);
        }
    }

    /// <summary>
    /// 清空全部条目（测试与诊断用）.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                _evictedBytes += entry.CachedBytes;
                entry.Blocks.Clear();
                entry.CachedBytes = 0;
            }

            _entries.Clear();
            _currentBytes = 0;
        }
    }

    internal void ReleaseLease(MediaRangeCacheEntry entry)
    {
        lock (_gate)
        {
            if (entry.LeaseCount > 0)
            {
                entry.LeaseCount--;
            }

            entry.LastAccess = ++_accessClock;

            // 空条目直接收掉：否则反复登记/释放（换集、重试）会攒一堆没有任何字节的空条目.
            if (entry.LeaseCount == 0 && entry.Blocks.Count == 0 && entry.CachedBytes <= 0)
            {
                _entries.Remove(entry.Identity);
            }
        }
    }

    internal long? GetLength(MediaRangeCacheEntry entry)
    {
        lock (_gate)
        {
            entry.LastAccess = ++_accessClock;
            return entry.Length >= 0 ? entry.Length : null;
        }
    }

    internal void SetLength(MediaRangeCacheEntry entry, long length)
    {
        if (length < 0)
        {
            return;
        }

        lock (_gate)
        {
            entry.Length = length;
            entry.LastAccess = ++_accessClock;
        }
    }

    internal byte[]? GetBlock(MediaRangeCacheEntry entry, int blockIndex)
    {
        lock (_gate)
        {
            entry.LastAccess = ++_accessClock;
            return entry.Blocks.TryGetValue(blockIndex, out var data) && IsBlockComplete(entry, blockIndex, data)
                ? data
                : null;
        }
    }

    internal bool ContainsBlock(MediaRangeCacheEntry entry, int blockIndex)
    {
        lock (_gate)
        {
            return entry.Blocks.TryGetValue(blockIndex, out var data) && IsBlockComplete(entry, blockIndex, data);
        }
    }

    internal int GetExpectedBlockLength(MediaRangeCacheEntry entry, int blockIndex)
    {
        lock (_gate)
        {
            return ComputeExpectedBlockLength(entry, blockIndex);
        }
    }

    /// <summary>
    /// 写入一个完整的块. 返回 false 表示没存进去（超预算且没有可淘汰条目，或数据不完整）.
    /// </summary>
    internal bool TryWriteBlock(MediaRangeCacheEntry entry, int blockIndex, ReadOnlySpan<byte> data)
    {
        if (blockIndex < 0 || data.Length <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            entry.LastAccess = ++_accessClock;
            var expected = ComputeExpectedBlockLength(entry, blockIndex);
            if (expected > 0 && data.Length != expected)
            {
                // 只收完整块：半块存进去会被当成命中，播放器就会读到错位的字节.
                return false;
            }

            if (expected <= 0 && data.Length != _blockSize)
            {
                // 长度未知时只接受满块.
                return false;
            }

            if (entry.Blocks.TryGetValue(blockIndex, out var existing))
            {
                if (existing.Length == data.Length)
                {
                    // 已经有同样的块，直接算成功（并发 flight 的常见情形）.
                    return true;
                }

                _currentBytes -= existing.Length;
            }

            var incoming = data.Length;
            if (!EnsureCapacityLocked(incoming, entry))
            {
                return false;
            }

            var copy = data.ToArray();
            entry.Blocks[blockIndex] = copy;
            entry.CachedBytes = entry.CachedBytes - (existing?.Length ?? 0) + copy.Length;
            _currentBytes += copy.Length;
            return true;
        }
    }

    internal IReadOnlyList<MediaByteRange> GetPresentRanges(MediaRangeCacheEntry entry, MediaByteRange window)
    {
        lock (_gate)
        {
            if (window.IsEmpty)
            {
                return [];
            }

            var (first, last) = MediaByteRanges.GetBlockSpan(window, _blockSize);
            if (first < 0)
            {
                return [];
            }

            var covered = new List<MediaByteRange>();
            for (var index = first; index <= last; index++)
            {
                if (!entry.Blocks.TryGetValue(index, out var data) || !IsBlockComplete(entry, index, data))
                {
                    continue;
                }

                var blockStart = (long)index * _blockSize;
                covered.Add(MediaByteRange.FromBounds(blockStart, blockStart + data.Length));
            }

            return MediaByteRanges.Intersect(covered, window);
        }
    }

    internal void AddServedFromCache(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _hitBytes += bytes;
        }
    }

    internal void AddFetchedFromUpstream(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _missBytes += bytes;
        }
    }

    private bool IsBlockComplete(MediaRangeCacheEntry entry, int blockIndex, byte[] data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        var expected = ComputeExpectedBlockLength(entry, blockIndex);
        return expected <= 0 ? data.Length > 0 : data.Length == expected;
    }

    private int ComputeExpectedBlockLength(MediaRangeCacheEntry entry, int blockIndex)
    {
        if (blockIndex < 0)
        {
            return 0;
        }

        if (entry.Length < 0)
        {
            return 0;
        }

        var blockStart = (long)blockIndex * _blockSize;
        if (blockStart >= entry.Length)
        {
            return 0;
        }

        var remaining = entry.Length - blockStart;
        return (int)Math.Min(_blockSize, remaining);
    }

    /// <summary>
    /// 为即将写入的 <paramref name="incoming"/> 字节腾地方；腾不出来返回 false.
    /// </summary>
    private bool EnsureCapacityLocked(long incoming, MediaRangeCacheEntry protectedEntry)
    {
        while (_currentBytes + incoming > _capacityBytes)
        {
            MediaRangeCacheEntry? victim = null;
            foreach (var candidate in _entries.Values)
            {
                if (ReferenceEquals(candidate, protectedEntry) || candidate.LeaseCount > 0 || candidate.CachedBytes <= 0)
                {
                    continue;
                }

                if (victim is null || candidate.LastAccess < victim.LastAccess)
                {
                    victim = candidate;
                }
            }

            if (victim is null)
            {
                return false;
            }

            _evictedBytes += victim.CachedBytes;
            _currentBytes -= victim.CachedBytes;
            victim.CachedBytes = 0;
            victim.Blocks.Clear();
            _evictionCount++;

            // 条目本身留着（可能还有租约之外的引用），只是块被清掉.
            if (victim.LeaseCount == 0)
            {
                _entries.Remove(victim.Identity);
            }
        }

        return true;
    }
}

/// <summary>
/// 缓存条目：一个逻辑媒体在缓存里的全部状态.
/// </summary>
internal sealed class MediaRangeCacheEntry(MediaStreamIdentity identity)
{
    public MediaStreamIdentity Identity { get; } = identity;

    public Dictionary<int, byte[]> Blocks { get; } = [];

    /// <summary>
    /// 资源总长度；-1 表示还不知道.
    /// </summary>
    public long Length { get; set; } = -1;

    public int LeaseCount { get; set; }

    public long LastAccess { get; set; }

    public long CachedBytes { get; set; }
}

/// <summary>
/// 缓存条目的租约：持有期间该条目不会被淘汰.
/// </summary>
public sealed class MediaCacheLease : IDisposable
{
    private readonly MediaRangeCache _owner;
    private readonly MediaRangeCacheEntry _entry;
    private int _disposed;

    internal MediaCacheLease(MediaRangeCache owner, MediaRangeCacheEntry entry)
    {
        _owner = owner;
        _entry = entry;
    }

    /// <summary>
    /// 这个租约对应的媒体身份.
    /// </summary>
    public MediaStreamIdentity Identity => _entry.Identity;

    /// <summary>
    /// 块大小（字节）.
    /// </summary>
    public int BlockSize => _owner.BlockSize;

    /// <summary>
    /// 资源总长度；未知时为 null. 由第一次向上游探测到的长度填进来，只写一次.
    /// </summary>
    public long? Length
    {
        get => _owner.GetLength(_entry);
        set
        {
            if (value is { } length)
            {
                _owner.SetLength(_entry, length);
            }
        }
    }

    /// <summary>
    /// 该块是否已完整缓存.
    /// </summary>
    /// <param name="blockIndex">块号.</param>
    /// <returns>是否命中.</returns>
    public bool ContainsBlock(int blockIndex) => _owner.ContainsBlock(_entry, blockIndex);

    /// <summary>
    /// 取一个完整的块（长度由总长度与块号算出）.
    /// </summary>
    /// <param name="blockIndex">块号.</param>
    /// <param name="block">块数据；未命中为 null.</param>
    /// <returns>是否命中.</returns>
    public bool TryGetBlock(int blockIndex, out byte[] block)
    {
        var data = _owner.GetBlock(_entry, blockIndex);
        block = data ?? [];
        return data is not null;
    }

    /// <summary>
    /// 写入一个完整的块；只收完整块，半块会被拒绝（返回 false）.
    /// </summary>
    /// <param name="blockIndex">块号.</param>
    /// <param name="data">块数据.</param>
    /// <returns>是否写入成功；false 表示这次没缓存（不影响播放）.</returns>
    public bool TryWriteBlock(int blockIndex, ReadOnlySpan<byte> data)
        => Volatile.Read(ref _disposed) == 0 && _owner.TryWriteBlock(_entry, blockIndex, data);

    /// <summary>
    /// 窗口内已经缓存好的区间.
    /// </summary>
    /// <param name="window">目标窗口.</param>
    /// <returns>命中区间.</returns>
    public IReadOnlyList<MediaByteRange> GetPresentRanges(MediaByteRange window)
        => _owner.GetPresentRanges(_entry, window);

    /// <summary>
    /// 窗口内还缺的区间.
    /// </summary>
    /// <param name="window">目标窗口.</param>
    /// <returns>缺失区间.</returns>
    public IReadOnlyList<MediaByteRange> GetMissingRanges(MediaByteRange window)
        => MediaByteRanges.Subtract(_owner.GetPresentRanges(_entry, window), window);

    /// <summary>
    /// 统计用：记 n 字节是从缓存里给出的.
    /// </summary>
    /// <param name="bytes">字节数.</param>
    public void MarkServedFromCache(long bytes) => _owner.AddServedFromCache(bytes);

    /// <summary>
    /// 统计用：记 n 字节是从上游 CDN 拉的.
    /// </summary>
    /// <param name="bytes">字节数.</param>
    public void MarkFetchedFromUpstream(long bytes) => _owner.AddFetchedFromUpstream(bytes);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.ReleaseLease(_entry);
    }
}

/// <summary>
/// 缓存统计快照.
/// </summary>
/// <param name="HitBytes">从缓存直接给出的字节数.</param>
/// <param name="MissBytes">向上游拉的字节数.</param>
/// <param name="EvictedBytes">被 LRU 淘汰掉的字节数.</param>
/// <param name="CurrentBytes">当前占用字节数.</param>
/// <param name="EntryCount">当前条目数.</param>
/// <param name="CachedBlockCount">当前缓存块数.</param>
/// <param name="EvictionCount">发生过多少次条目淘汰.</param>
/// <param name="CapacityBytes">容量上限.</param>
/// <param name="BlockSize">块大小.</param>
public readonly record struct MediaCacheStatistics(
    long HitBytes,
    long MissBytes,
    long EvictedBytes,
    long CurrentBytes,
    int EntryCount,
    int CachedBlockCount,
    int EvictionCount,
    long CapacityBytes,
    int BlockSize)
{
    /// <inheritdoc/>
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"hit={HitBytes / 1024}KiB miss={MissBytes / 1024}KiB evicted={EvictedBytes / 1024}KiB used={CurrentBytes / 1024}KiB entries={EntryCount} blocks={CachedBlockCount} evictions={EvictionCount} cap={CapacityBytes / 1024 / 1024}MiB");
}
