// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 从弹幕文本里识别「跳伞 12:34」「跳至 12:34」「12:34」这类时间戳。
/// 只认整条弹幕就是时间戳（或前面带跳伞/跳至/空降/跳转），避免把正文里的
/// "12:34" 也当成跳转点.
/// </summary>
public static partial class DanmakuJumpDetector
{
    [GeneratedRegex(@"^\s*(?:跳伞|跳至|跳转|空降|跳到)\s*[:：]?\s*(\d{1,2})\s*[:：]\s*(\d{2})\s*$")]
    private static partial Regex PrefixedPattern();

    [GeneratedRegex(@"^\s*(\d{1,2})\s*[:：]\s*(\d{2})\s*$")]
    private static partial Regex BarePattern();

    [GeneratedRegex(@"^\s*(?:跳伞|跳至|跳转|空降|跳到)\s*[:：]?\s*(\d{1,2})\s*[:：]\s*(\d{2})\s*[:：]\s*(\d{2})\s*$")]
    private static partial Regex PrefixedHourPattern();

    /// <summary>
    /// 尝试解析跳转目标时间.
    /// </summary>
    /// <param name="text">弹幕文本.</param>
    /// <param name="seconds">目标时间（秒）.</param>
    /// <returns>是否识别成功.</returns>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = PrefixedHourPattern().Match(text);
        if (match.Success
            && TryGet(match, 1, out var h)
            && TryGet(match, 2, out var m)
            && TryGet(match, 3, out var s))
        {
            seconds = (h * 3600) + (m * 60) + s;
            return true;
        }

        match = PrefixedPattern().Match(text);
        if (match.Success && TryGet(match, 1, out var minute) && TryGet(match, 2, out var second))
        {
            seconds = (minute * 60) + second;
            return true;
        }

        match = BarePattern().Match(text);
        if (match.Success && TryGet(match, 1, out var bareMinute) && TryGet(match, 2, out var bareSecond))
        {
            seconds = (bareMinute * 60) + bareSecond;
            return true;
        }

        return false;
    }

    private static bool TryGet(Match match, int group, out double value)
    {
        value = 0;
        return double.TryParse(
            match.Groups[group].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }
}
