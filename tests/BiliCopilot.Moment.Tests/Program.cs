// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Toolkits;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }

    checks++;
}

var tracker = new PlaybackWatchTracker();
for (var second = 0; second <= 60; second++)
{
    Check(!tracker.Observe(second, TimeSpan.FromSeconds(second), true, 1), "60 秒及以下不能标记已看");
}

Check(tracker.Observe(61, TimeSpan.FromSeconds(61), true, 1), "超过 60 秒应标记已看");

tracker = new PlaybackWatchTracker();
tracker.Observe(0, TimeSpan.Zero, true, 1);
Check(!tracker.Observe(600, TimeSpan.FromSeconds(1), true, 1), "拖到第十分钟不能标记已看");
for (var second = 2; second <= 60; second++)
{
    Check(!tracker.Observe(599 + second, TimeSpan.FromSeconds(second), true, 1), "跳转不能计入观看时长");
}

tracker = new PlaybackWatchTracker();
for (var second = 0; second <= 30; second++)
{
    tracker.Observe(second, TimeSpan.FromSeconds(second), true, 1);
}

tracker.Observe(30, TimeSpan.FromSeconds(31), false, 1);
tracker.Observe(30, TimeSpan.FromSeconds(200), true, 1);
Check(!tracker.Observe(31, TimeSpan.FromSeconds(201), true, 1), "暂停时长不能计入观看时长");
Check(!tracker.Observe(500, TimeSpan.FromSeconds(240), true, 1), "缓冲及长时间缺少采样不能计入");

tracker = new PlaybackWatchTracker();
for (var second = 0; second <= 60; second++)
{
    Check(!tracker.Observe(second * 2, TimeSpan.FromSeconds(second), true, 2), "倍速播放仍应累计实际观看时间");
}

Check(tracker.Observe(122, TimeSpan.FromSeconds(61), true, 2), "倍速实际观看超过一分钟应标记");

var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.FromHours(8));
DateTimeOffset Cutoff(string tip) => MomentTimelineToolkit.GetPublishLowerBound(null, tip, now);
Check(Cutoff("昨天 · 投稿视频") == new DateTimeOffset(2026, 1, 1, 0, 0, 0, now.Offset), "昨天应从零点扫描");
Check(Cutoff("12-31 · 投稿视频").Year == 2025, "跨年提示应识别为去年");
Check(Cutoff("2025年12月30日 · 投稿视频").Day == 30, "应支持中文日期");
Check(Cutoff("2025-12-30 16:20 · 投稿视频").Year == 2025, "应支持完整日期");
Check(Cutoff("2小时前 · 投稿视频") < now.AddHours(-2), "相对时间必须给出保守下界");
Check(Cutoff("刚刚 · 投稿视频").Hour == 0, "刚刚应扫描当日记录");
Check(Cutoff("未知时间") == DateTimeOffset.MinValue, "未知时间不能提前停止历史扫描");
Check(MomentTimelineToolkit.GetPublishLowerBound(now.AddDays(-3), "刚刚", now) == now.AddDays(-3), "应优先使用完整发布时间");

Check(!MomentTimelineToolkit.IsWatchedProgress(60, 300), "历史进度恰好一分钟不能算已看");
Check(MomentTimelineToolkit.IsWatchedProgress(61, 300), "历史进度超过一分钟应算已看");
Check(!MomentTimelineToolkit.IsWatchedProgress(null, 300), "无进度记录不能算已看");
Check(!MomentTimelineToolkit.IsWatchedProgress(-1, 60), "一分钟以内的已播完短视频不能算已看");
Check(MomentTimelineToolkit.IsWatchedProgress(-1, 61), "超过一分钟且已播完的视频应算已看");
Check(MomentTimelineToolkit.IsInWatchedWindow(now.AddDays(-1), 5, now, 3, false), "右侧应包含锚点之前发布的视频");
Check(!MomentTimelineToolkit.IsInWatchedWindow(now.AddDays(1), 1, now, 3, false), "下翻后右侧不应显示较新的视频");
Check(MomentTimelineToolkit.IsInWatchedWindow(now, 1, now, 3, false), "相同发布时间的视频应保留");
Check(!MomentTimelineToolkit.IsInWatchedWindow(null, 1, null, 3, false), "无精确时间时应沿用动态顺序");
Check(MomentTimelineToolkit.IsInWatchedWindow(null, 4, null, 3, false), "无精确时间时应显示锚点之后的动态");
Check(MomentTimelineToolkit.IsInWatchedWindow(now.AddDays(1), 1, now, 3, true), "页面顶部应显示最新已看动态");


Console.WriteLine($"Passed {checks} moment playback and timeline checks.");
