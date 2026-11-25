// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace BiliCopilot.UI.Services.Sponsor;

/// <summary>
/// 跳过片段表的规划器：把四路来源的原始片段洗干净、排好、合并成一张能直接执行的表.
/// <para>
/// 为什么单独抽出来：这段逻辑原来埋在 <c>PlayerViewModel.MergeSegments</c> 里，
/// 是「重叠/相邻/倒挂/超时长/负数/禁用类别」这些边界唯一该被验证的地方，
/// 放在 ViewModel 里就只能靠手点视频试 —— 这里做成纯函数，<c>tests/MediaCache.Tests</c> 直接跑.
/// </para>
/// <para>
/// 规划规则（顺序即执行顺序）：
/// <list type="number">
/// <item>丢掉 <see cref="double.IsNaN"/> / 无穷大的时间点；丢掉负数起点.</item>
/// <item>丢掉 <c>Start &gt;= End</c>（含相等：零长度区间没法跳，留着只会让 tick 空转）.</item>
/// <item>给了时长时：丢掉整个落在时长之外的区间；丢掉终点超过时长的区间
/// （终点超出说明这条数据不可信 —— 拿它去跳会被播放器拒掉，夹到片尾又会误跳最后一段，
/// 所以宁可不跳，也不猜）.</item>
/// <item>给了启用类别集合时：丢掉用户关掉的类别.</item>
/// <item>按起点排序；同一个「类别 + 来源」且相互重叠或间隔小于
/// <c>mergeGapSeconds</c> 的合成一段.</item>
/// </list>
/// </para>
/// <para>
/// 合并为什么限定「同类别 + 同来源」：赞助广告和三连提醒常常首尾贴着，
/// 合到一块会让结束时间顶到片尾，跳转目标超出时长直接被拒 —— 这是这条规则存在的实测原因.
/// </para>
/// </summary>
public static class SegmentPlanner
{
    /// <summary>
    /// 默认的「相邻」判定：间隔小于 1 秒就算同一段.
    /// </summary>
    public const double DefaultMergeGapSeconds = 1d;

    /// <summary>
    /// 规划一张片段表.
    /// </summary>
    /// <param name="segments">原始片段（可为 null / 空，结果为空的表）.</param>
    /// <param name="mediaDuration">当前媒体时长（秒）；&lt;= 0 表示未知，此时不做时长校验
    /// （不要在时长还不知道时把有效片段全丢掉）.</param>
    /// <param name="mergeGapSeconds">间隔小于它的相邻段合并；&lt; 0 按 0 处理（只合并重叠）.</param>
    /// <param name="enabledCategories">用户启用的类别集合；null 或空表示不按类别过滤.</param>
    /// <returns>按起点升序、互不重叠的片段表（新对象，不复用输入实例）.</returns>
    public static IReadOnlyList<SponsorSegment> Plan(
        IEnumerable<SponsorSegment>? segments,
        double mediaDuration = 0,
        double mergeGapSeconds = DefaultMergeGapSeconds,
        IReadOnlySet<string>? enabledCategories = null)
    {
        if (segments is null)
        {
            return [];
        }

        var gap = double.IsNaN(mergeGapSeconds) || mergeGapSeconds < 0 ? 0 : mergeGapSeconds;
        var hasDuration = !double.IsNaN(mediaDuration) && mediaDuration > 0;
        var filterCategories = enabledCategories is { Count: > 0 };

        var accepted = new List<SponsorSegment>();
        foreach (var segment in segments)
        {
            if (segment is null)
            {
                continue;
            }

            var start = segment.Start;
            var end = segment.End;
            if (double.IsNaN(start) || double.IsNaN(end) || double.IsInfinity(start) || double.IsInfinity(end))
            {
                continue;
            }

            // 负数起点：数据坏了（或时间轴用错了），不要拿它去 seek.
            if (start < 0)
            {
                continue;
            }

            if (start >= end)
            {
                continue;
            }

            if (hasDuration)
            {
                // 整段在时长之外，或者终点越过时长：都当不可信数据处理.
                if (start >= mediaDuration || end > mediaDuration)
                {
                    continue;
                }
            }

            if (filterCategories && !enabledCategories!.Contains(segment.Category))
            {
                continue;
            }

            accepted.Add(new SponsorSegment
            {
                Start = start,
                End = end,
                Category = segment.Category,
                Source = segment.Source,
                ActionType = segment.ActionType,
                Note = segment.Note,
                Detail = segment.Detail,
            });
        }

        accepted.Sort(static (left, right) =>
        {
            var byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : left.End.CompareTo(right.End);
        });

        var merged = new List<SponsorSegment>(accepted.Count);
        foreach (var segment in accepted)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var sameKind = string.Equals(last.Category, segment.Category, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(last.Source, segment.Source, StringComparison.OrdinalIgnoreCase);
                if (sameKind && segment.Start <= last.End + gap)
                {
                    last.End = Math.Max(last.End, segment.End);
                    continue;
                }
            }

            merged.Add(segment);
        }

        return merged;
    }

    /// <summary>
    /// 日志用摘要（不含 URL / Cookie）.
    /// </summary>
    /// <param name="segments">片段表.</param>
    /// <returns>如 <c>sponsor 12.0-30.0(空降助手) / intro 0.0-5.5(空降助手)</c>.</returns>
    public static string Describe(IReadOnlyList<SponsorSegment>? segments)
    {
        if (segments is null || segments.Count == 0)
        {
            return "（无）";
        }

        return string.Join(
            " / ",
            segments.Select(p => string.Create(
                CultureInfo.InvariantCulture,
                $"{p.Category} {p.Start:F1}-{p.End:F1}s({p.Source})")));
    }
}
