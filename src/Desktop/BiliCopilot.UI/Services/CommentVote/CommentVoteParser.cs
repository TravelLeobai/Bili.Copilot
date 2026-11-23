// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text.Json;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 读 <c>/x/vote/vote_info</c> 与 <c>/x/vote/do_vote</c> 的 JSON。
/// <para>
/// 服务端会省掉值为 0 或空的字段（0 票的选项没有 <c>cnt</c>），字段也可能给字符串，
/// 所以统一按「读不到就当 0/空」处理.
/// </para>
/// </summary>
internal static class CommentVoteParser
{
    // DateTimeOffset.FromUnixTimeSeconds 最大到 9999-12-31，超过就是脏数据.
    private const long MaxUnixSeconds = 253402300799;

    /// <summary>
    /// 从响应的 <c>data</c> 里读一个投票.
    /// </summary>
    /// <param name="data">响应里的 data 节点.</param>
    /// <returns>投票；没有可用投票（比如已删除）时返回 null.</returns>
    public static CommentVoteDetail? ParseDetail(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("vote_info", out var info)
            || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetInt64(info, "vote_id");
        var title = GetString(info, "title");
        var options = ParseOptions(info);

        // 已删除的投票只有 {"status":4}：id/标题/选项都不在，这里按「不可用」返回.
        if (id <= 0 || string.IsNullOrWhiteSpace(title) || options.Count == 0)
        {
            return null;
        }

        var endTime = GetInt64(info, "end_time");
        var publisher = GetInt64(info, "vote_publisher");

        // 登录时自己的选择先在 vote_info 旁边，投过之后会进 vote_info 里面.
        var myVotes = ParseIndexes(data, "my_votes");
        if (myVotes.Count == 0)
        {
            myVotes = ParseIndexes(info, "my_votes");
        }

        return new CommentVoteDetail(id.ToString(CultureInfo.InvariantCulture), title!.Trim(), options)
        {
            Description = GetString(info, "desc") is { } description && !string.IsNullOrWhiteSpace(description)
                ? description.Trim()
                : null,
            EndTime = endTime is > 0 and <= MaxUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(endTime) : null,
            MaxChoiceCount = (int)Math.Max(1, Math.Min(options.Count, GetInt64(info, "choice_cnt"))),
            ParticipantCount = Math.Max(0, GetInt64(info, "join_num")),
            MyVotes = myVotes,
            PublisherId = publisher > 0 ? publisher.ToString(CultureInfo.InvariantCulture) : null,
            PublisherName = GetString(info, "name"),
            IsSupporterOnly = GetInt64(info, "only_fans_level") >= 10,
        };
    }

    /// <summary>
    /// 单独读选项。投票的响应可能只带新票数，标题等信息还得用旧的.
    /// </summary>
    /// <param name="info">带 options 的节点.</param>
    /// <returns>选项；没有就返回空集合.</returns>
    public static IReadOnlyList<CommentVoteOption> ParseOptions(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<CommentVoteOption>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var index = GetInt64(option, "opt_idx");
            if (index is <= 0 or > int.MaxValue)
            {
                continue;
            }

            result.Add(new CommentVoteOption(
                (int)index,
                GetString(option, "opt_desc")?.Trim() ?? string.Empty,
                Math.Max(0, GetInt64(option, "cnt")),
                ParseImage(GetString(option, "img_url"))));
        }

        return result;
    }

    /// <summary>
    /// 读一串选项序号（比如当前用户投了哪几项）.
    /// </summary>
    /// <param name="owner">所在节点.</param>
    /// <param name="name">字段名.</param>
    /// <returns>序号；只留正数并去重.</returns>
    public static IReadOnlyList<int> ParseIndexes(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(name, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<int>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var index)
                && index > 0
                && !result.Contains(index))
            {
                result.Add(index);
            }
        }

        return result;
    }

    /// <summary>
    /// 读一个整数，数字和字符串都认；读不到就是 0.
    /// </summary>
    /// <param name="owner">所在节点.</param>
    /// <param name="name">字段名.</param>
    /// <returns>数值.</returns>
    public static long GetInt64(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>
    /// 读一个字符串；不是字符串就是 null.
    /// </summary>
    /// <param name="owner">所在节点.</param>
    /// <param name="name">字段名.</param>
    /// <returns>文本.</returns>
    public static string? GetString(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object
            && owner.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? ParseImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // 图片地址可能是 //i0.hdslb.com/… 或 http://，统一升成 https（打包应用里明文 http 画不出来）.
        var text = url.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            text = "https:" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        }

        return uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }
}
