// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.Toolkits;

/// <summary>
/// 为仅有时间提示的动态计算保守的发布时间下界，避免漏掉观看记录.
/// </summary>
internal static partial class MomentTimelineToolkit
{
    public static bool IsWatchedProgress(int? progress, long? duration)
        => progress > 60 || (progress == -1 && duration > 60);

    public static bool IsInWatchedWindow(DateTimeOffset? publishTime, int index,
        DateTimeOffset? anchorTime, int anchorIndex, bool isAtTop)
        => isAtTop || anchorIndex < 0
            || (publishTime is { } time && anchorTime is { } anchor
                ? time <= anchor
                : index >= anchorIndex);

    public static DateTimeOffset GetPublishLowerBound(DateTimeOffset? publishTime, string? tip)
        => GetPublishLowerBound(publishTime, tip, DateTimeOffset.Now);

    internal static DateTimeOffset GetPublishLowerBound(DateTimeOffset? publishTime, string? tip, DateTimeOffset now)
    {
        if (publishTime is { } time)
        {
            return time;
        }

        tip ??= string.Empty;
        if (tip.Contains("刚刚", StringComparison.Ordinal) || tip.Contains("今天", StringComparison.Ordinal))
        {
            return new DateTimeOffset(now.Date, now.Offset);
        }

        if (tip.Contains("昨天", StringComparison.Ordinal))
        {
            return new DateTimeOffset(now.Date.AddDays(-1), now.Offset);
        }

        if (tip.Contains("前天", StringComparison.Ordinal))
        {
            return new DateTimeOffset(now.Date.AddDays(-2), now.Offset);
        }

        var relative = RelativeTimeRegex().Match(tip);
        if (relative.Success && int.TryParse(relative.Groups[1].Value, out var amount))
        {
            var seconds = relative.Groups[2].Value switch
            {
                "秒" => 1,
                "分钟" => 60,
                "小时" => 3600,
                _ => 86400,
            };
            return now.AddSeconds(-((double)amount + 1) * seconds).AddDays(-1);
        }

        var date = DateRegex().Match(tip);
        if (date.Success)
        {
            var text = date.Value.Replace('年', '-').Replace('月', '-').TrimEnd('日');
            var hasYear = date.Groups[1].Success;
            if (!hasYear)
            {
                text = $"{now.Year}-{text}";
            }

            if (DateTime.TryParseExact(text, ["yyyy-M-d", "yyyy-MM-dd", "yyyy-M-dd", "yyyy-MM-d"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                if (!hasYear && parsed.Date > now.Date)
                {
                    parsed = parsed.AddYears(-1);
                }

                return new DateTimeOffset(parsed, now.Offset);
            }
        }

        // 未识别的提示不能作为停止扫描的依据.
        return DateTimeOffset.MinValue;
    }

    [GeneratedRegex(@"(\d+)\s*(秒|分钟|小时|天)前")]
    private static partial Regex RelativeTimeRegex();

    [GeneratedRegex(@"(?:(\d{4})[-年])?\d{1,2}[-月]\d{1,2}日?")]
    private static partial Regex DateRegex();
}
