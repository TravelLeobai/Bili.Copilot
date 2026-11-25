// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 一个半开区间 [Start, Start+Length) 描述的单段字节范围.
/// 用「起点 + 长度」而不是「起点 + 终点」，是因为 RFC 7233 的 Content-Range 与
/// 缓存块对齐计算都更直接（Length 为 0 即空区间，不会出现 End &lt; Start 的怪状态）.
/// </summary>
public readonly record struct MediaByteRange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaByteRange"/> struct.
    /// </summary>
    /// <param name="start">起始偏移（含）.</param>
    /// <param name="length">长度，负数按 0 处理.</param>
    public MediaByteRange(long start, long length)
    {
        Start = start;
        Length = length < 0 ? 0 : length;
    }

    /// <summary>
    /// 起始偏移（含）.
    /// </summary>
    public long Start { get; }

    /// <summary>
    /// 字节数.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// 起始偏移（含），等价于 <see cref="Start"/>，便于阅读.
    /// </summary>
    public long EndInclusive => Start + Length - 1;

    /// <summary>
    /// 结束偏移（不含）.
    /// </summary>
    public long EndExclusive => Start + Length;

    /// <summary>
    /// 是否为空区间.
    /// </summary>
    public bool IsEmpty => Length <= 0;

    /// <summary>
    /// 构造区间.
    /// </summary>
    /// <param name="start">起始偏移（含）.</param>
    /// <param name="endExclusive">结束偏移（不含）.</param>
    /// <returns>区间.</returns>
    public static MediaByteRange FromBounds(long start, long endExclusive)
        => new(start, endExclusive - start);

    /// <summary>
    /// 与另一个区间是否有重叠（都非空时）.
    /// </summary>
    /// <param name="other">另一个区间.</param>
    /// <returns>是否重叠.</returns>
    public bool Intersects(MediaByteRange other)
        => !IsEmpty && !other.IsEmpty && Start < other.EndExclusive && other.Start < EndExclusive;

    /// <summary>
    /// 与另一个区间首尾相接或重叠（用于合并）.
    /// </summary>
    /// <param name="other">另一个区间.</param>
    /// <returns>是否可合并.</returns>
    public bool IsAdjacentOrIntersects(MediaByteRange other)
        => !IsEmpty && !other.IsEmpty && Start <= other.EndExclusive && other.Start <= EndExclusive;

    /// <inheritdoc/>
    public override string ToString()
        => IsEmpty
            ? "empty"
            : string.Create(CultureInfo.InvariantCulture, $"[{Start},{EndExclusive})");
}

/// <summary>
/// HTTP Range 头的解析结果.
/// </summary>
public enum MediaRangeParseResult
{
    /// <summary>
    /// 没有 Range 头，或者 Range 头为空 —— 按整段返回 200.
    /// </summary>
    None,

    /// <summary>
    /// 解析出一段合法范围.
    /// </summary>
    Valid,

    /// <summary>
    /// 语法合法但超出总长度 —— 应回 416.
    /// </summary>
    Unsatisfiable,

    /// <summary>
    /// 无法按单段范围处理（语法错误，或多段 Range）—— 调用方应忽略 Range 按 200 处理.
    /// </summary>
    Unsupported,
}

/// <summary>
/// Range 头的解析与区间集合运算（纯逻辑，方便单测）.
/// </summary>
public static class MediaByteRanges
{
    /// <summary>
    /// 解析 <c>Range: bytes=...</c>.
    /// <para>
    /// 只支持单段范围：格式错误的输入返回 <see cref="MediaRangeParseResult.Unsupported"/>，
    /// 多段（带逗号）也返回 Unsupported，让调用方按「忽略 Range、整段 200」处理 ——
    /// 这是 RFC 7233 允许的服务端行为，也避免给播放器造 multipart/byteranges 这种没人用的响应.
    /// </para>
    /// </summary>
    /// <param name="header">Range 头原文，可为 null.</param>
    /// <param name="totalLength">资源总长度；未知时传 null（此时不做越界判定）.</param>
    /// <param name="range">解析出的范围.</param>
    /// <returns>解析结果.</returns>
    public static MediaRangeParseResult TryParse(string? header, long? totalLength, out MediaByteRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(header))
        {
            return MediaRangeParseResult.None;
        }

        var text = header.Trim();
        if (!text.StartsWith("bytes", StringComparison.OrdinalIgnoreCase))
        {
            return MediaRangeParseResult.Unsupported;
        }

        var equalsIndex = text.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex < 0)
        {
            return MediaRangeParseResult.Unsupported;
        }

        var spec = text[(equalsIndex + 1)..].Trim();
        if (spec.Length == 0 || spec.Contains(',', StringComparison.Ordinal))
        {
            return MediaRangeParseResult.Unsupported;
        }

        var dashIndex = spec.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex < 0)
        {
            return MediaRangeParseResult.Unsupported;
        }

        var startText = spec[..dashIndex].Trim();
        var endText = spec[(dashIndex + 1)..].Trim();

        if (startText.Length == 0)
        {
            // bytes=-N：最后 N 个字节.
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return MediaRangeParseResult.Unsupported;
            }

            if (totalLength is null)
            {
                return MediaRangeParseResult.Unsupported;
            }

            if (totalLength.Value <= 0)
            {
                return MediaRangeParseResult.Unsatisfiable;
            }

            var length = Math.Min(suffix, totalLength.Value);
            range = new MediaByteRange(totalLength.Value - length, length);
            return MediaRangeParseResult.Valid;
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start < 0)
        {
            return MediaRangeParseResult.Unsupported;
        }

        long endInclusive;
        if (endText.Length == 0)
        {
            // bytes=N-：到结尾.
            if (totalLength is null)
            {
                return MediaRangeParseResult.Unsupported;
            }

            endInclusive = totalLength.Value - 1;
        }
        else if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out endInclusive) || endInclusive < start)
        {
            return MediaRangeParseResult.Unsupported;
        }

        if (totalLength is not null)
        {
            var total = totalLength.Value;
            if (start >= total)
            {
                return MediaRangeParseResult.Unsatisfiable;
            }

            endInclusive = Math.Min(endInclusive, total - 1);
        }

        range = new MediaByteRange(start, endInclusive - start + 1);
        return MediaRangeParseResult.Valid;
    }

    /// <summary>
    /// 排序并合并重叠 / 首尾相接的区间.
    /// </summary>
    /// <param name="ranges">待合并区间.</param>
    /// <returns>合并后的区间（按起点升序，互不相接）.</returns>
    public static IReadOnlyList<MediaByteRange> Merge(IEnumerable<MediaByteRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var ordered = ranges.Where(p => !p.IsEmpty).OrderBy(p => p.Start).ThenBy(p => p.Length).ToList();
        if (ordered.Count <= 1)
        {
            return ordered;
        }

        var merged = new List<MediaByteRange>(ordered.Count);
        var currentStart = ordered[0].Start;
        var currentEnd = ordered[0].EndExclusive;
        for (var i = 1; i < ordered.Count; i++)
        {
            var item = ordered[i];
            if (item.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, item.EndExclusive);
                continue;
            }

            merged.Add(MediaByteRange.FromBounds(currentStart, currentEnd));
            currentStart = item.Start;
            currentEnd = item.EndExclusive;
        }

        merged.Add(MediaByteRange.FromBounds(currentStart, currentEnd));
        return merged;
    }

    /// <summary>
    /// 求 <paramref name="window"/> 内没有被 <paramref name="covered"/> 覆盖的部分（即缺失区间）.
    /// </summary>
    /// <param name="covered">已覆盖区间（可乱序、可重叠）.</param>
    /// <param name="window">目标窗口.</param>
    /// <returns>缺失区间（按起点升序，互不相接）.</returns>
    public static IReadOnlyList<MediaByteRange> Subtract(IEnumerable<MediaByteRange> covered, MediaByteRange window)
    {
        ArgumentNullException.ThrowIfNull(covered);
        if (window.IsEmpty)
        {
            return [];
        }

        var merged = Merge(covered);
        var missing = new List<MediaByteRange>();
        var cursor = window.Start;
        foreach (var item in merged)
        {
            if (item.EndExclusive <= cursor)
            {
                continue;
            }

            if (item.Start >= window.EndExclusive)
            {
                break;
            }

            if (item.Start > cursor)
            {
                missing.Add(MediaByteRange.FromBounds(cursor, item.Start));
            }

            cursor = Math.Max(cursor, item.EndExclusive);
            if (cursor >= window.EndExclusive)
            {
                break;
            }
        }

        if (cursor < window.EndExclusive)
        {
            missing.Add(MediaByteRange.FromBounds(cursor, window.EndExclusive));
        }

        return missing;
    }

    /// <summary>
    /// 求 <paramref name="window"/> 内已被 <paramref name="covered"/> 覆盖的部分（即命中区间）.
    /// </summary>
    /// <param name="covered">已覆盖区间（可乱序、可重叠）.</param>
    /// <param name="window">目标窗口.</param>
    /// <returns>命中区间（已裁剪到窗口内，按起点升序）.</returns>
    public static IReadOnlyList<MediaByteRange> Intersect(IEnumerable<MediaByteRange> covered, MediaByteRange window)
    {
        ArgumentNullException.ThrowIfNull(covered);
        if (window.IsEmpty)
        {
            return [];
        }

        var result = new List<MediaByteRange>();
        foreach (var item in Merge(covered))
        {
            var start = Math.Max(item.Start, window.Start);
            var end = Math.Min(item.EndExclusive, window.EndExclusive);
            if (end > start)
            {
                result.Add(MediaByteRange.FromBounds(start, end));
            }
        }

        return result;
    }

    /// <summary>
    /// 把字节区间切成块索引区间（按 <paramref name="blockSize"/> 对齐的闭区间块号）.
    /// </summary>
    /// <param name="range">目标区间.</param>
    /// <param name="blockSize">块大小.</param>
    /// <returns>首块号与末块号；空区间返回 (-1, -1).</returns>
    public static (int FirstBlock, int LastBlock) GetBlockSpan(MediaByteRange range, int blockSize)
    {
        if (range.IsEmpty || blockSize <= 0)
        {
            return (-1, -1);
        }

        var first = (int)(range.Start / blockSize);
        var last = (int)((range.EndExclusive - 1) / blockSize);
        return (first, last);
    }
}
