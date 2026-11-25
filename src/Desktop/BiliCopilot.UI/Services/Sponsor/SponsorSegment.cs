// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services;

/// <summary>
/// 需要跳过的片段（SponsorBlock / 云端广告库 / Bilijump / AI 识别的统一形状）.
/// <para>
/// 放在 <c>Services/Sponsor/</c> 下、命名空间仍是 <c>BiliCopilot.UI.Services</c>：
/// 这个类型全仓被引用（四个来源 + 播放器），改命名空间只会制造一堆无意义的 using；
/// 挪出 SponsorBlockService 是为了让区间规划（<c>SegmentPlanner</c>）能脱离
/// WinUI / 设置 / 日志单独编译与单测.
/// </para>
/// </summary>
public sealed class SponsorSegment
{
    /// <summary>
    /// 开始时间（秒）.
    /// </summary>
    public double Start { get; set; }

    /// <summary>
    /// 结束时间（秒）.
    /// </summary>
    public double End { get; set; }

    /// <summary>
    /// 片段分类（sponsor / intro / ai / cloud 等）.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// 片段来源（sponsorblock / cloud / ai）.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 备注（Bilijump 云端库给的广告主名称；其它来源为空）.
    /// </summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// 细节（Bilijump 云端库给的广告内容描述；其它来源为空）.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// 动作类型（skip / mute / full），对齐 SponsorBlock 的 actionType.
    /// </summary>
    public string ActionType { get; set; } = "skip";

    /// <summary>
    /// 分类的可读名字（借用 biliuwp-lite 的映射）.
    /// </summary>
    public string CategoryName => Category switch
    {
        "sponsor" => "赞助广告",
        "intro" => "开场动画",
        "outro" => "片尾",
        "selfpromo" => "自我推广",
        "interaction" => "三连提醒",
        "preview" => "预告回顾",
        "music_offtopic" => "非音乐部分",
        "poi_highlight" => "精彩时刻",
        "filler" => "填充内容",
        "ai" => "AI 识别",
        "cloud" => "云端数据",
        _ => Category,
    };

    /// <summary>
    /// 时长.
    /// </summary>
    public double Duration => Math.Max(0, End - Start);
}
