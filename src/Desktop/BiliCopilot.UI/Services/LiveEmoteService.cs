// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 直播表情：房间专属表情 + UP 主大表情.
/// 接口 xlive/web-ucenter/v2/emoticon/GetEmoticons，需要登录态才知道能解锁哪些包.
/// </summary>
internal static class LiveEmoteService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly Dictionary<string, List<LiveEmotePackage>> RoomCache = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// 拉取某个直播间的表情包（带缓存）.
    /// </summary>
    /// <param name="roomId">直播间 id.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="logger">日志.</param>
    /// <returns>表情包列表.</returns>
    public static async Task<List<LiveEmotePackage>> GetRoomEmotesAsync(string roomId, string cookie, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return [];
        }

        lock (RoomCache)
        {
            if (RoomCache.TryGetValue(roomId, out var cached) && cached.Count > 0)
            {
                return cached;
            }
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (RoomCache)
            {
                if (RoomCache.TryGetValue(roomId, out var cached) && cached.Count > 0)
                {
                    return cached;
                }
            }

            var url = $"https://api.live.bilibili.com/xlive/web-ucenter/v2/emoticon/GetEmoticons?platform=pc&room_id={roomId}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", $"https://live.bilibili.com/{roomId}");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("data", out var packages)
                || packages.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<LiveEmotePackage>();
            foreach (var package in packages.EnumerateArray())
            {
                var name = package.TryGetProperty("pkg_name", out var pkgName) ? pkgName.GetString() ?? string.Empty : string.Empty;
                if (!package.TryGetProperty("emoticons", out var emoticons) || emoticons.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var items = new List<LiveEmote>();
                foreach (var emote in emoticons.EnumerateArray())
                {
                    var token = emote.TryGetProperty("emoji", out var emojiNode) ? emojiNode.GetString() ?? string.Empty : string.Empty;
                    var emoteUrl = emote.TryGetProperty("url", out var urlNode) ? urlNode.GetString() ?? string.Empty : string.Empty;
                    var descript = emote.TryGetProperty("descript", out var descNode) ? descNode.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(emoteUrl))
                    {
                        continue;
                    }

                    items.Add(new LiveEmote(token, emoteUrl, descript));
                }

                if (items.Count > 0)
                {
                    result.Add(new LiveEmotePackage(string.IsNullOrWhiteSpace(name) ? "直播间表情" : name, items));
                }
            }

            lock (RoomCache)
            {
                RoomCache[roomId] = result;
            }

            logger?.LogInformation("直播表情已加载：{Packages} 个包，共 {Count} 个表情", result.Count, result.Sum(p => p.Emotes.Count));
            return result;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "拉取直播表情失败");
            return [];
        }
        finally
        {
            Gate.Release();
        }
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

/// <summary>
/// 直播表情包.
/// </summary>
/// <param name="Name">包名.</param>
/// <param name="Emotes">表情列表.</param>
internal sealed record LiveEmotePackage(string Name, List<LiveEmote> Emotes);

/// <summary>
/// 单个直播表情.
/// </summary>
/// <param name="Token">发送用的占位文本（就是 emoji 字段）.</param>
/// <param name="Url">图片地址.</param>
/// <param name="Description">描述.</param>
internal sealed record LiveEmote(string Token, string Url, string Description);
