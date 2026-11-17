// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 给"观看兴趣分析"取样本视频的字幕节选。
/// <para>
/// 顺序是：先要上传字幕，没有就用 B 站自动生成的 AI 字幕，两者都没有就跳过这个视频。
/// B 站把两者都放在 <c>x/player/v2</c> 的 <c>data.subtitle.subtitles</c> 里，
/// AI 字幕的地址落在 <c>aisubtitle.hdslb.com/bfs/ai_subtitle/...</c> 下，
/// 所以一次接口就能分辨，不用额外请求。
/// </para>
/// </summary>
internal static class SubtitleSampler
{
    /// <summary>
    /// 单个视频最多取多少字，塞进提示词时还要再裁.
    /// </summary>
    private const int SnippetLength = 320;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// 抓若干视频的字幕节选。
    /// </summary>
    /// <param name="bvidList">视频 bvid（按兴趣样本顺序，最多取前几个）.</param>
    /// <param name="cookie">登录态（AI 字幕要它）.</param>
    /// <param name="logger">日志.</param>
    /// <param name="maxCount">最多抓几个视频，默认 3，抓太多会拖慢兴趣分析.</param>
    /// <returns>bvid → 字幕节选；没抓到的不在结果里.</returns>
    public static async Task<Dictionary<string, string>> CollectAsync(
        IEnumerable<string> bvidList,
        string cookie,
        ILogger? logger = null,
        int maxCount = 3)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = bvidList
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
        if (ids.Count == 0)
        {
            return result;
        }

        foreach (var bvid in ids)
        {
            try
            {
                var snippet = await FetchAsync(bvid, cookie, logger).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(snippet))
                {
                    result[bvid] = snippet;
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "抓字幕节选失败：{Bvid}", bvid);
            }
        }

        logger?.LogInformation(
            "兴趣分析：字幕采样 {Hit}/{Total} 命中（{Ids}）",
            result.Count,
            ids.Count,
            string.Join(',', result.Keys));

        return result;
    }

    /// <summary>
    /// 抓一个视频的字幕节选：上传字幕优先，其次 AI 字幕，都没有返回 null.
    /// </summary>
    /// <param name="bvid">视频 bvid.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="logger">日志.</param>
    /// <returns>字幕正文节选；没有字幕返回 null.</returns>
    private static async Task<string?> FetchAsync(string bvid, string cookie, ILogger? logger)
    {
        if (!bvid.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cid = await ResolveCidAsync(bvid, cookie).ConfigureAwait(false);
        if (string.IsNullOrEmpty(cid))
        {
            return null;
        }

        var url = $"https://api.bilibili.com/x/player/v2?bvid={bvid}&cid={cid}";
        var json = await GetAsync(url, cookie).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var candidates = new List<(string Url, bool IsAi, double Weight)>();
        using (var document = JsonDocument.Parse(json))
        {
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("subtitle", out var subtitle)
                || !subtitle.TryGetProperty("subtitles", out var subtitles)
                || subtitles.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in subtitles.EnumerateArray())
            {
                var subUrl = item.TryGetProperty("subtitle_url", out var urlNode) ? urlNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(subUrl))
                {
                    continue;
                }

                if (subUrl.StartsWith("//", StringComparison.Ordinal))
                {
                    subUrl = "https:" + subUrl;
                }

                var isAi = subUrl.Contains("ai_subtitle", StringComparison.OrdinalIgnoreCase);
                var weight = item.TryGetProperty("weight", out var weightNode) && weightNode.TryGetDouble(out var w) ? w : 0;
                candidates.Add((subUrl, isAi, weight));
            }
        }

        if (candidates.Count == 0)
        {
            logger?.LogDebug("兴趣分析：{Bvid} 没有字幕（上传和 AI 都没有），跳过", bvid);
            return null;
        }

        // 上传字幕优先；都只有 AI 字幕时也能用，那本来就是 B 站给生成的那份.
        foreach (var candidate in candidates.OrderBy(p => p.IsAi).ThenByDescending(p => p.Weight))
        {
            var body = await FetchBodyAsync(candidate.Url, cookie).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                logger?.LogDebug(
                    "兴趣分析：{Bvid} 用{Kind}字幕，取 {Length} 字",
                    bvid,
                    candidate.IsAi ? "AI " : "上传",
                    body.Length);
                return body;
            }
        }

        return null;
    }

    /// <summary>
    /// 把 bvid 换成 cid（player/v2 要 cid）.
    /// </summary>
    /// <param name="bvid">视频 bvid.</param>
    /// <param name="cookie">登录态.</param>
    /// <returns>cid；拿不到返回 null.</returns>
    private static async Task<string?> ResolveCidAsync(string bvid, string cookie)
    {
        var json = await GetAsync($"https://api.bilibili.com/x/web-interface/view?bvid={bvid}", cookie).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            if (data.TryGetProperty("cid", out var cidNode))
            {
                return cidNode.ValueKind == JsonValueKind.Number
                    ? cidNode.GetInt64().ToString()
                    : cidNode.GetString();
            }

            if (data.TryGetProperty("pages", out var pages)
                && pages.ValueKind == JsonValueKind.Array
                && pages.GetArrayLength() > 0
                && pages[0].TryGetProperty("cid", out var firstCid))
            {
                return firstCid.GetInt64().ToString();
            }
        }
        catch
        {
            // 解析失败按没取到处理.
        }

        return null;
    }

    /// <summary>
    /// 下载字幕 JSON 并压成一段纯文本（丢掉时间轴）.
    /// </summary>
    /// <param name="url">字幕地址.</param>
    /// <param name="cookie">登录态.</param>
    /// <returns>正文节选；失败返回 null.</returns>
    private static async Task<string?> FetchBodyAsync(string url, string cookie)
    {
        var json = await GetAsync(url, cookie).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var item in body.EnumerateArray())
        {
            var text = item.TryGetProperty("content", out var textNode) ? textNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.Append(text).Append(' ');
            if (builder.Length >= SnippetLength)
            {
                break;
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var snippet = builder.ToString().Trim();
        return snippet.Length > SnippetLength ? snippet[..SnippetLength] : snippet;
    }

    private static async Task<string?> GetAsync(string url, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
            : null;
    }
}
