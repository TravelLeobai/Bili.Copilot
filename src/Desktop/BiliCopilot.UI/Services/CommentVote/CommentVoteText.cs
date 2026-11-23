// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 评论正文里的投票占位符.
/// <para>
/// 带投票的评论，正文里会留一个 <c>{vote:123}</c>。内核给的是 NuGet 包、没有源码，
/// 拿不到 <c>Content.vote</c>，所以只能从正文里把 id 抠出来，再走 vote_info 补全。
/// </para>
/// <para>
/// 抠不出可用 id 的标记（<c>{vote:}</c>、<c>{vote:abc}</c>、超出 long 的 id）也要从正文里去掉，
/// 否则用户会看到一串原始占位符；只是这种评论不显示投票入口。
/// </para>
/// </summary>
internal static partial class CommentVoteText
{
    /// <summary>
    /// 从正文里抠出投票 id.
    /// </summary>
    /// <param name="text">评论正文.</param>
    /// <param name="voteId">抠到的投票 id（正规化成十进制字符串），没有时为空串.</param>
    /// <returns>是否抠到一个可用的投票 id.</returns>
    public static bool TryExtractVoteId(string? text, out string voteId)
    {
        voteId = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var match = StrictMarker().Match(text);
        if (!match.Success)
        {
            return false;
        }

        // 只认正整数。超长数字 long 解析不出来，按不可用处理（不抛异常）.
        if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return false;
        }

        voteId = id.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// 判断正文里是否带投票占位符（不管 id 能不能用）.
    /// </summary>
    /// <param name="text">评论正文.</param>
    /// <returns>是否带占位符.</returns>
    public static bool HasVotePlaceholder(string? text)
        => !string.IsNullOrEmpty(text) && AnyMarker().IsMatch(text);

    /// <summary>
    /// 去掉正文里所有投票占位符.
    /// <para>
    /// 没有占位符时原样返回（一个字符都不动，也不 Trim）；真的剥掉了才整理首尾空白，
    /// 否则「{vote:123}你好」会以空格开头.
    /// </para>
    /// </summary>
    /// <param name="text">评论正文.</param>
    /// <returns>去掉占位符之后的正文.</returns>
    public static string StripVotePlaceholders(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        if (!AnyMarker().IsMatch(text))
        {
            return text;
        }

        return AnyMarker().Replace(text, string.Empty).Trim();
    }

    // 严格：{vote:123}，允许冒号两侧有空白，大小写不敏感.
    [GeneratedRegex(@"\{\s*vote\s*:\s*([0-9]+)\s*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrictMarker();

    // 宽松：任何 {vote:…} 都算占位符，用来剥正文（包括 id 非法的）.
    [GeneratedRegex(@"\{\s*vote\s*:[^}]*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyMarker();
}
