// Copyright (c) Bili Copilot. All rights reserved.

using System.Buffers.Binary;
using System.Globalization;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// DASH 段的 SIDX（Segment Index Box）解析结果：把「时间」换算成「字节」。
/// <para>
/// 为什么需要它：预取的目标是「前 N 秒」，而缓存与代理是按字节工作的。
/// 只有 SIDX 里的 <c>referenced_size / subsegment_duration</c> 能给出真实映射；
/// 只看标称码率估算会因为场景切换、码率自适应而偏出一大截
/// （偏小 → 前 N 秒没拉够；偏大 → 白拉一段没人看的数据）。
/// </para>
/// <para>
/// 解析失败（不是 fMP4、IndexRange 拿不到、字段越界）时返回 null，
/// 调用方退回按码率估算并在日志里标「估算」。
/// </para>
/// </summary>
public sealed class MediaSegmentIndex
{
    /// <summary>
    /// 每个引用段的大小（字节）.
    /// </summary>
    private readonly long[] _sizes;

    /// <summary>
    /// 每个引用段的时长（Timescale 单位）.
    /// </summary>
    private readonly long[] _durations;

    /// <summary>
    /// 累计时长（秒），长度与 <see cref="_sizes"/> 相同，第 i 项表示前 i+1 段的累计时长.
    /// </summary>
    private readonly double[] _cumulativeSeconds;

    private readonly double[] _cumulativeBytes;

    private MediaSegmentIndex(
        int version,
        long referenceId,
        int timescale,
        long earliestPresentationTime,
        long firstOffset,
        long[] sizes,
        long[] durations,
        int nestedIndexCount)
    {
        Version = version;
        ReferenceId = referenceId;
        Timescale = timescale;
        EarliestPresentationTime = earliestPresentationTime;
        FirstOffset = firstOffset;
        _sizes = sizes;
        _durations = durations;
        NestedIndexCount = nestedIndexCount;

        _cumulativeSeconds = new double[sizes.Length];
        _cumulativeBytes = new double[sizes.Length];
        double seconds = 0;
        double bytes = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            seconds += durations[i] / (double)timescale;
            bytes += sizes[i];
            _cumulativeSeconds[i] = seconds;
            _cumulativeBytes[i] = bytes;
        }
    }

    /// <summary>
    /// SIDX 版本（0 或 1）.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// reference_ID.
    /// </summary>
    public long ReferenceId { get; }

    /// <summary>
    /// timescale（每秒多少个时间单位）.
    /// </summary>
    public int Timescale { get; }

    /// <summary>
    /// earliest_presentation_time.
    /// </summary>
    public long EarliestPresentationTime { get; }

    /// <summary>
    /// first_offset：SIDX 盒结尾到第一个子段起始的距离.
    /// </summary>
    public long FirstOffset { get; }

    /// <summary>
    /// 引用段数量.
    /// </summary>
    public int ReferenceCount => _sizes.Length;

    /// <summary>
    /// 引用到「另一个 SIDX」的条数（分层索引；B 站 dash 里通常为 0，
    /// 非 0 时时间→字节的线性映射只能算近似）.
    /// </summary>
    public int NestedIndexCount { get; }

    /// <summary>
    /// 整段可用时长（秒）.
    /// </summary>
    public double TotalDurationSeconds => _cumulativeSeconds.Length == 0 ? 0 : _cumulativeSeconds[^1];

    /// <summary>
    /// 覆盖 <paramref name="seconds"/> 秒所需的字节数（向上取整到整段）.
    /// </summary>
    /// <param name="seconds">目标秒数.</param>
    /// <returns>字节数；秒数 &lt;= 0 时返回 0.</returns>
    public long GetByteLengthForSeconds(double seconds)
    {
        if (seconds <= 0 || _cumulativeSeconds.Length == 0)
        {
            return 0;
        }

        if (seconds >= TotalDurationSeconds)
        {
            return (long)_cumulativeBytes[^1];
        }

        var index = Array.BinarySearch(_cumulativeSeconds, seconds);
        if (index >= 0)
        {
            // 正好落在段边界上：这一段的字节也算进去（含边界更安全）.
            return (long)_cumulativeBytes[index];
        }

        var insertion = ~index;
        return insertion >= _cumulativeBytes.Length
            ? (long)_cumulativeBytes[^1]
            : (long)_cumulativeBytes[insertion];
    }

    /// <summary>
    /// 把时间点映射到「某个子段」的字节区间.
    /// 偏移量以媒体数据起点（即 <c>indexEnd + FirstOffset</c>）为 0 计算.
    /// </summary>
    /// <param name="seconds">相对媒体起点的秒数.</param>
    /// <param name="mediaDataStart">媒体数据在文件里的绝对起点.</param>
    /// <param name="start">子段绝对起点.</param>
    /// <param name="length">子段字节数.</param>
    /// <returns>是否命中（时间超出总时长时返回最后一个子段）.</returns>
    public bool TryMapTime(double seconds, long mediaDataStart, out long start, out long length)
    {
        start = 0;
        length = 0;
        if (_sizes.Length == 0)
        {
            return false;
        }

        if (seconds < 0)
        {
            seconds = 0;
        }

        var index = Array.BinarySearch(_cumulativeSeconds, seconds);
        if (index < 0)
        {
            index = ~index;
        }

        index = Math.Clamp(index, 0, _sizes.Length - 1);
        var offset = index == 0 ? 0 : (long)_cumulativeBytes[index - 1];
        start = mediaDataStart + offset;
        length = _sizes[index];
        return true;
    }

    /// <summary>
    /// 解析一段 SIDX 字节（通常是 dash 的 IndexRange 内容）.
    /// </summary>
    /// <param name="data">原始字节.</param>
    /// <param name="error">失败原因（成功时为 null）.</param>
    /// <returns>解析结果；失败返回 null.</returns>
    public static MediaSegmentIndex? TryParse(ReadOnlySpan<byte> data, out string? error)
    {
        error = null;
        if (data.Length < 16)
        {
            error = "数据太短";
            return null;
        }

        var fourcc = FindSidx(data);
        if (fourcc < 4)
        {
            error = "没找到 sidx 盒";
            return null;
        }

        var boxStart = fourcc - 4;
        var boxSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(boxStart, 4));
        if (boxSize < 16 || boxStart + boxSize > data.Length)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"盒长度不合法：{boxSize}");
            return null;
        }

        var box = data.Slice(boxStart, (int)boxSize);
        var pos = 8;
        var version = box[pos];
        pos += 4; // version + flags

        if (pos + 8 > box.Length)
        {
            error = "头部被截断";
            return null;
        }

        var referenceId = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(pos, 4));
        pos += 4;
        var timescale = BinaryPrimitives.ReadInt32BigEndian(box.Slice(pos, 4));
        pos += 4;
        if (timescale <= 0)
        {
            error = "timescale 非法";
            return null;
        }

        long earliest;
        long firstOffset;
        if (version == 0)
        {
            if (pos + 8 > box.Length)
            {
                error = "version 0 头部被截断";
                return null;
            }

            earliest = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(pos, 4));
            pos += 4;
            firstOffset = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(pos, 4));
            pos += 4;
        }
        else
        {
            if (pos + 16 > box.Length)
            {
                error = "version 1 头部被截断";
                return null;
            }

            earliest = (long)BinaryPrimitives.ReadUInt64BigEndian(box.Slice(pos, 8));
            pos += 8;
            firstOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(box.Slice(pos, 8));
            pos += 8;
        }

        pos += 2; // reserved
        if (pos + 2 > box.Length)
        {
            error = "reference_count 被截断";
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(box.Slice(pos, 2));
        pos += 2;
        if (count == 0)
        {
            error = "reference_count 为 0";
            return null;
        }

        if (pos + (count * 12) > box.Length)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"引用表被截断：count={count}");
            return null;
        }

        var sizes = new long[count];
        var durations = new long[count];
        var nested = 0;
        for (var i = 0; i < count; i++)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(pos, 4));
            pos += 4;
            var referenceType = (word >> 31) & 0x1;
            var referencedSize = word & 0x7FFFFFFF;
            var duration = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(pos, 4));
            pos += 4;
            pos += 4; // starts_with_SAP + SAP 类型 + SAP 时间
            if (referenceType == 1)
            {
                nested++;
            }

            sizes[i] = referencedSize;
            durations[i] = duration;
        }

        return new MediaSegmentIndex(version, referenceId, timescale, earliest, firstOffset, sizes, durations, nested);
    }

    /// <summary>
    /// 解析 dash 里的 <c>Initialization</c> / <c>IndexRange</c> 字段（形如 <c>0-1100</c>）.
    /// </summary>
    /// <param name="text">字段原文.</param>
    /// <param name="range">解析出的字节范围（闭区间换算成半开区间）.</param>
    /// <returns>是否解析成功.</returns>
    public static bool TryParseDashRange(string? text, out MediaByteRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        var dash = span.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        if (!long.TryParse(span[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return false;
        }

        if (!long.TryParse(span[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var endInclusive))
        {
            return false;
        }

        if (start < 0 || endInclusive < start)
        {
            return false;
        }

        range = new MediaByteRange(start, endInclusive - start + 1);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"sidx(v{Version}) refs={ReferenceCount} duration={TotalDurationSeconds:F1}s bytes={_cumulativeBytes[^1]:F0} offset={FirstOffset} nested={NestedIndexCount}");

    private static int FindSidx(ReadOnlySpan<byte> data)
    {
        for (var i = 4; i + 4 <= data.Length; i++)
        {
            if (data[i] == (byte)'s' && data[i + 1] == (byte)'i' && data[i + 2] == (byte)'d' && data[i + 3] == (byte)'x')
            {
                return i;
            }
        }

        return -1;
    }
}
