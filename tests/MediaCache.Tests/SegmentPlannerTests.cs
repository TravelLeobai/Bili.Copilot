// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services;
using BiliCopilot.UI.Services.Sponsor;

namespace MediaCache.Tests;

/// <summary>
/// 区间规划（合并 + 校验）的 6 类边界：重叠 / 相邻 / 倒挂 / 超时长 / 负数 / 禁用类别.
/// </summary>
internal static class SegmentPlannerTests
{
    public static void Register()
    {
        TestHost.Add("规划：重叠区间合并成一段", () =>
        {
            var result = SegmentPlanner.Plan([
                Segment(10, 20, "sponsor", "sponsorblock"),
                Segment(15, 25, "sponsor", "sponsorblock"),
            ]);

            Assert.Equal(1, result.Count, "应合成一段");
            Assert.Equal(10d, result[0].Start, "起点取最前");
            Assert.Equal(25d, result[0].End, "终点取最后");
        });

        TestHost.Add("规划：间隔小于 1 秒的相邻段合并，超过 1 秒的不合并", () =>
        {
            var near = SegmentPlanner.Plan([
                Segment(10, 20, "sponsor", "sponsorblock"),
                Segment(20.5, 30, "sponsor", "sponsorblock"),
            ]);
            Assert.Equal(1, near.Count, "0.5s 间隔应合并");
            Assert.Equal(30d, near[0].End, "合并后终点");

            var far = SegmentPlanner.Plan([
                Segment(10, 20, "sponsor", "sponsorblock"),
                Segment(22, 30, "sponsor", "sponsorblock"),
            ]);
            Assert.Equal(2, far.Count, "2s 间隔不该合并");
        });

        TestHost.Add("规划：Start >= End 的倒挂区间丢弃", () =>
        {
            var result = SegmentPlanner.Plan([
                Segment(10, 10, "sponsor", "sponsorblock"),
                Segment(20, 15, "sponsor", "sponsorblock"),
                Segment(30, 40, "sponsor", "sponsorblock"),
            ]);

            Assert.Equal(1, result.Count, "只剩合法那一段");
            Assert.Equal(30d, result[0].Start, "剩下的是 30-40");
        });

        TestHost.Add("规划：超出视频时长的区间丢弃（整段在外 / 终点越界都算）", () =>
        {
            var result = SegmentPlanner.Plan(
                [
                    Segment(90, 100, "sponsor", "sponsorblock"),
                    Segment(95, 9999, "sponsor", "sponsorblock"),
                    Segment(150, 160, "sponsor", "sponsorblock"),
                ],
                mediaDuration: 100);

            Assert.Equal(1, result.Count, "只有 90-100 落在时长内");
            Assert.Equal(90d, result[0].Start, "保留段的起点");
            Assert.Equal(100d, result[0].End, "保留段的终点正好等于时长");
        });

        TestHost.Add("规划：时长未知（<= 0）时不做时长校验，不能把有效段全丢掉", () =>
        {
            var result = SegmentPlanner.Plan([Segment(90, 9999, "sponsor", "sponsorblock")], mediaDuration: 0);

            Assert.Equal(1, result.Count, "时长未知时应保留");
        });

        TestHost.Add("规划：负数 / NaN 时间点丢弃", () =>
        {
            var result = SegmentPlanner.Plan([
                Segment(-5, 10, "sponsor", "sponsorblock"),
                Segment(5, -1, "sponsor", "sponsorblock"),
                Segment(double.NaN, 20, "sponsor", "sponsorblock"),
                Segment(12, double.NaN, "sponsor", "sponsorblock"),
                Segment(1, 2, "sponsor", "sponsorblock"),
            ]);

            Assert.Equal(1, result.Count, "只剩 1-2");
            Assert.Equal(1d, result[0].Start, "起点");
        });

        TestHost.Add("规划：用户禁用的类别被过滤，白名单里的来源类别保留", () =>
        {
            // 白名单 = 用户启用的类别 + AI / 云端这类「没有独立类别开关」的来源类别.
            var allowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sponsor", "ai", "cloud" };
            var result = SegmentPlanner.Plan(
                [
                    Segment(10, 20, "sponsor", "sponsorblock"),
                    Segment(30, 40, "intro", "sponsorblock"),
                    Segment(50, 60, "ai", "ai"),
                    Segment(70, 80, "cloud", "cloud"),
                ],
                enabledCategories: allowList);

            Assert.Equal(3, result.Count, "intro 被用户关掉，其余保留");
            Assert.Equal("sponsor", result[0].Category, "第 1 段类别");
            Assert.Equal("ai", result[1].Category, "第 2 段类别");
            Assert.Equal("cloud", result[2].Category, "第 3 段类别");
        });

        TestHost.Add("规划：不同类别 / 不同来源即使贴着也不合并（否则会把片尾顶穿）", () =>
        {
            var result = SegmentPlanner.Plan([
                Segment(10, 20, "sponsor", "sponsorblock"),
                Segment(20.2, 25, "interaction", "sponsorblock"),
                Segment(25.1, 30, "sponsor", "cloud"),
            ]);

            Assert.Equal(3, result.Count, "三类各不相同，各留一段");
        });

        TestHost.Add("规划：合并时保留备注 / 动作类型 / 输出的顺序按起点升序", () =>
        {
            var withNote = Segment(30, 40, "sponsor", "bilijump");
            withNote.Note = "某广告主";
            withNote.Detail = "某广告文案";
            withNote.ActionType = "mute";

            var result = SegmentPlanner.Plan([
                withNote,
                Segment(10, 20, "sponsor", "sponsorblock"),
            ]);

            Assert.Equal(2, result.Count, "两段");
            Assert.Equal(10d, result[0].Start, "先输出早的那段");
            Assert.Equal(30d, result[1].Start, "后输出晚的那段");
            Assert.Equal("某广告主", result[1].Note, "备注应保留");
            Assert.Equal("某广告文案", result[1].Detail, "细节应保留");
            Assert.Equal("mute", result[1].ActionType, "动作类型应保留");
        });

        TestHost.Add("规划：合并后不能改到输入对象（纯函数，不复用实例）", () =>
        {
            var original = Segment(10, 20, "sponsor", "sponsorblock");
            var extra = Segment(15, 25, "sponsor", "sponsorblock");

            var result = SegmentPlanner.Plan([original, extra]);

            Assert.Equal(20d, original.End, "输入对象不应被改写");
            Assert.Equal(25d, result[0].End, "结果才是合并后的");
        });

        TestHost.Add("规划：空输入 / null 输入给出空表", () =>
        {
            Assert.Equal(0, SegmentPlanner.Plan(null).Count, "null");
            Assert.Equal(0, SegmentPlanner.Plan([]).Count, "空表");
        });

        TestHost.Add("规划：负的合并间隔按 0 处理（只合并重叠）", () =>
        {
            var result = SegmentPlanner.Plan(
                [
                    Segment(10, 20, "sponsor", "sponsorblock"),
                    Segment(20.5, 30, "sponsor", "sponsorblock"),
                ],
                mergeGapSeconds: -5);

            Assert.Equal(2, result.Count, "间隔 0.5s 不该合并");
        });
    }

    private static SponsorSegment Segment(double start, double end, string category, string source)
        => new()
        {
            Start = start,
            End = end,
            Category = category,
            Source = source,
        };
}
