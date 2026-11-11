// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text.Json;
using BiliCopilot.UI.Services;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 从评论原始 JSON 的 content.emote 里补表情.
/// 内核给的 EmoteText.Emotes 只有常用表情，装扮表情只能从回复原文里拿，
/// 这里按页把 token → 图片地址灌进 BiliEmoteService，渲染层就能命中.
/// </summary>
internal static class CommentEmoteEnricher
{
    private static readonly HttpClient Client = CreateClient();

    private static readonly HashSet<string> FetchedPages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 拉一页评论的 emote 映射并注册到表情服务.
    /// </summary>
    /// <param name="oid">评论对象 id（视频 aid）.  </param>
    /// <param name="targetType">评论类型（1 视频 / 12 专栏 …）.</param>
    /// <param name="page">页码（从 1 开始）.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="logger">日志.</param>
    /// <param name="sort">排序（2 热门 / 0 时间），必须与列表一致，否则取到的条目对不上.</param>
    /// <returns>任务.</returns>
    public static async Task EnrichAsync(string oid, int targetType, int page, string cookie, ILogger? logger = null, int sort = 2)
    {
        if (string.IsNullOrWhiteSpace(oid) || page <= 0)
        {
            return;
        }

        var key = $"{targetType}:{oid}:{page}:{sort}";
        lock (FetchedPages)
        {
            if (!FetchedPages.Add(key))
            {
                return;
            }
        }

        var url = $"https://api.bilibili.com/x/v2/reply?type={targetType}&oid={oid}&sort={sort}&ps=20&pn={page}&nohot=1";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("replies", out var replies)
                || replies.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var added = 0;
            foreach (var reply in replies.EnumerateArray())
            {
                added += RegisterReplyEmotes(reply);
            }

            if (added > 0)
            {
                logger?.LogInformation("评论表情补充完成：{Count} 个（{Oid} 第 {Page} 页）", added, oid, page);
                BiliEmoteService.SaveCache();
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "补充评论表情失败");
        }
    }

    private static int RegisterReplyEmotes(JsonElement reply)
    {
        var added = 0;

        // 顶层回复自身的表情.
        added += RegisterFromContent(reply);

        // 楼中楼里的表情也算，避免楼中楼里再出现占位.
        if (reply.TryGetProperty("replies", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                added += RegisterFromContent(child);
            }
        }

        return added;
    }

    private static int RegisterFromContent(JsonElement reply)
    {
        if (!reply.TryGetProperty("content", out var content)
            || !content.TryGetProperty("emote", out var emote)
            || emote.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var added = 0;
        foreach (var entry in emote.EnumerateObject())
        {
            var token = entry.Name;
            if (!entry.Value.TryGetProperty("url", out var urlElement))
            {
                continue;
            }

            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var size = 1;
            if (entry.Value.TryGetProperty("meta", out var meta)
                && meta.TryGetProperty("size", out var sizeElement)
                && sizeElement.TryGetInt32(out var parsed))
            {
                size = parsed;
            }

            if (BiliEmoteService.AddDynamic(token, url!, size))
            {
                added++;
            }
        }

        return added;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
    }
}





