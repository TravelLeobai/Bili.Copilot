// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 直播间接口封装.
/// </summary>
public static class LiveRoomApi
{
    private const string LiveApi = "https://api.live.bilibili.com";
    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// 取直播间详情（标题、主播、人气、粉丝数、开播时间等）.
    /// </summary>
    public static async Task<LiveRoomDetail?> GetRoomDetailAsync(string roomId, string cookie, ILogger? logger = null)
    {
        var json = await GetJsonAsync($"{LiveApi}/xlive/web-room/v1/index/getInfoByRoom?room_id={roomId}", cookie, logger).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return null;
        }

        var detail = new LiveRoomDetail();
        if (data.TryGetProperty("room_info", out var room))
        {
            detail.RoomId = ReadString(room, "room_id");
            detail.ShortRoomId = ReadString(room, "short_id");
            detail.Title = ReadString(room, "title");
            detail.IsLive = ReadInt(room, "live_status") == 1;
            detail.Online = ReadInt(room, "online");
            detail.AreaName = ReadString(room, "area_name");
            detail.ParentAreaName = ReadString(room, "parent_area_name");
            detail.StartTime = ReadLong(room, "live_start_time");
            detail.AnchorUid = ReadLong(room, "uid");
            detail.FansText = ReadString(room, "watched_show") is { Length: > 0 } ? detail.FansText : string.Empty;
        }

        if (data.TryGetProperty("anchor_info", out var anchor))
        {
            if (anchor.TryGetProperty("base_info", out var baseInfo))
            {
                detail.AnchorName = ReadString(baseInfo, "uname");
                detail.AnchorAvatar = ReadString(baseInfo, "face");
            }

            if (anchor.TryGetProperty("relation_info", out var relation))
            {
                detail.FansCount = ReadLong(relation, "attention");
            }
        }

        return detail;
    }

    /// <summary>
    /// 取直播流的画质与线路.
    /// </summary>
    public static async Task<IReadOnlyList<LiveStreamOption>> GetStreamOptionsAsync(string roomId, string cookie, ILogger? logger = null)
    {
        var url = $"{LiveApi}/xlive/web-room/v2/index/getRoomPlayInfo?room_id={roomId}&protocol=0,1&format=0,1,2&codec=0,1&qn=10000&platform=web&ptype=8";
        var json = await GetJsonAsync(url, cookie, logger).ConfigureAwait(false);
        if (json is null)
        {
            return Array.Empty<LiveStreamOption>();
        }

        var options = new List<LiveStreamOption>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("playurl_info", out var playUrlInfo)
            || playUrlInfo.ValueKind != JsonValueKind.Object
            || !playUrlInfo.TryGetProperty("playurl", out var playUrl)
            || !playUrl.TryGetProperty("stream", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return options;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            var protocol = ReadString(stream, "protocol_name");
            if (!stream.TryGetProperty("format", out var formats) || formats.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var format in formats.EnumerateArray())
            {
                var formatName = ReadString(format, "format_name");
                if (!format.TryGetProperty("codec", out var codecs) || codecs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var codec in codecs.EnumerateArray())
                {
                    if (!codec.TryGetProperty("url_info", out var urlInfos) || urlInfos.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var baseUrl = ReadString(codec, "base_url");
                    var currentQn = ReadInt(codec, "current_qn");
                    var acceptQn = new List<int>();
                    if (codec.TryGetProperty("accept_qn", out var acceptNode) && acceptNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var qn in acceptNode.EnumerateArray())
                        {
                            acceptQn.Add(qn.GetInt32());
                        }
                    }

                    var lineIndex = 0;
                    foreach (var urlInfo in urlInfos.EnumerateArray())
                    {
                        var host = ReadString(urlInfo, "host");
                        var extra = ReadString(urlInfo, "extra");
                        if (string.IsNullOrEmpty(host))
                        {
                            continue;
                        }

                        options.Add(new LiveStreamOption
                        {
                            Protocol = protocol,
                            Format = formatName,
                            Codec = ReadString(codec, "codec_name"),
                            Quality = currentQn,
                            AcceptQualities = acceptQn,
                            LineIndex = lineIndex++,
                            Url = $"{host}{baseUrl}{extra}",
                        });
                    }
                }
            }
        }

        // 同一画质+线路会同时给出 HLS 与 FLV，优先保留 HLS 并去重.
        return options
            .OrderBy(p => p.Protocol == "http_hls" ? 0 : 1)
            .GroupBy(p => (p.Quality, p.LineIndex))
            .Select(g => g.First())
            .OrderByDescending(p => p.Quality)
            .ThenBy(p => p.LineIndex)
            .ToList();
    }

    /// <summary>
    /// 取直播间礼物榜.
    /// </summary>
    public static async Task<IReadOnlyList<LiveRankItem>> GetGoldRankAsync(string roomId, long anchorUid, string cookie, ILogger? logger = null)
    {
        var url = $"{LiveApi}/xlive/general-interface/v1/rank/getOnlineGoldRank?ruid={anchorUid}&roomId={roomId}&page=1&pageSize=20";
        var json = await GetJsonAsync(url, cookie, logger).ConfigureAwait(false);
        var result = new List<LiveRankItem>();
        if (json is null)
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !TryGetArray(data, out var items, "OnlineRankItem", "onlineRankItem")
            || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            var rank = ReadInt(item, "userRank");
            if (rank <= 0)
            {
                rank = ReadInt(item, "rank");
            }

            result.Add(new LiveRankItem
            {
                Rank = rank,
                UserName = ReadString(item, "name"),
                Avatar = ReadString(item, "face"),
                Score = ReadLong(item, "score"),
                IsGuard = item.TryGetProperty("guard_level", out var guard) && guard.GetInt32() > 0,
            });
        }

        return result;
    }

    /// <summary>
    /// 取大航海（舰长）列表.
    /// </summary>
    public static async Task<IReadOnlyList<LiveGuardItem>> GetGuardListAsync(string roomId, long anchorUid, string cookie, ILogger? logger = null)
    {
        var url = $"{LiveApi}/xlive/app-room/v2/guardTab/topList?roomid={roomId}&ruid={anchorUid}&page=1&page_size=20";
        var json = await GetJsonAsync(url, cookie, logger).ConfigureAwait(false);
        var result = new List<LiveGuardItem>();
        if (json is null)
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("list", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            result.Add(new LiveGuardItem
            {
                UserName = ReadString(item, "username"),
                Avatar = ReadString(item, "face"),
                GuardLevel = ReadInt(item, "guard_level"),
                TotalWealth = ReadLong(item, "total_wealth"),
            });
        }

        return result;
    }

    /// <summary>
    /// 取直播间礼物背包（免费礼物）.
    /// </summary>
    public static async Task<IReadOnlyList<LiveGiftBagItem>> GetGiftBagAsync(string roomId, string cookie, ILogger? logger = null)
    {
        var url = $"{LiveApi}/xlive/general-interface/v1/gift/getBag?room_id={roomId}";
        var json = await GetJsonAsync(url, cookie, logger).ConfigureAwait(false);
        var result = new List<LiveGiftBagItem>();
        if (json is null)
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("list", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            result.Add(new LiveGiftBagItem
            {
                GiftName = ReadString(item, "gift_name"),
                GiftId = ReadString(item, "gift_id"),
                Number = ReadInt(item, "gift_num"),
                CoinType = ReadString(item, "coin_type"),
                Price = ReadInt(item, "price"),
                Image = ReadString(item, "img_basic"),
            });
        }

        return result;
    }

    /// <summary>
    /// 取钱包余额（金瓜子/银瓜子）.
    /// </summary>
    public static async Task<(long Gold, long Silver)> GetWalletAsync(string cookie, ILogger? logger = null)
    {
        var json = await GetJsonAsync($"{LiveApi}/xlive/revenue/v1/wallet/getList", cookie, logger).ConfigureAwait(false);
        if (json is null)
        {
            return (0, 0);
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return (0, 0);
        }

        return (ReadLong(data, "gold"), ReadLong(data, "silver"));
    }

    /// <summary>
    /// 发送直播弹幕（REST 兜底，WS 不通时使用）.
    /// </summary>
    public static async Task<bool> SendDanmakuAsync(string roomId, string text, string cookie, string csrf, ILogger? logger = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.live.bilibili.com/msg/send");
            request.Headers.TryAddWithoutValidation("Referer", $"https://live.bilibili.com/{roomId}");
            request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["bubble"] = "0",
                ["color"] = "16777215",
                ["fontsize"] = "25",
                ["mode"] = "1",
                ["msg"] = text,
                ["rnd"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(),
                ["roomid"] = roomId,
                ["csrf"] = csrf,
                ["csrf_token"] = csrf,
            });

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var code = -1;
            var message = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("code", out var codeNode))
                {
                    code = codeNode.GetInt32();
                }

                if (document.RootElement.TryGetProperty("message", out var msgNode))
                {
                    message = msgNode.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                logger?.LogWarning("直播弹幕返回不是 JSON：{Body}", body.Length > 200 ? body[..200] : body);
            }

            logger?.LogInformation("直播弹幕发送返回：code={Code} message={Message}", code, message);
            if (code == 0)
            {
                return true;
            }

            // 10030 内容被拒、10031 频率过快、1003212 未实名，都是服务端明确拒绝.
            logger?.LogWarning("直播弹幕被拒：code={Code} message={Message}", code, message);
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "发送直播弹幕失败");
            return false;
        }
    }

    /// <summary>
    /// 兜底取主播 UID（房间接口可能拿不到时用）.
    /// </summary>
    public static async Task<long> GetAnchorUidAsync(string roomId, string cookie, ILogger? logger = null)
    {
        var json = await GetJsonAsync($"{LiveApi}/room/v1/Room/room_init?id={roomId}", cookie, logger).ConfigureAwait(false);
        if (json is null)
        {
            return 0;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("data", out var data) ? ReadLong(data, "uid") : 0;
    }

    private static async Task<string?> GetJsonAsync(string url, string cookie, ILogger? logger)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "直播接口请求失败：{Url}", url);
            return null;
        }
    }

/// <summary>
    /// 按多个候选字段名取数组（接口字段大小写前后不一致）.
    /// </summary>
    private static bool TryGetArray(JsonElement element, out JsonElement array, params string[] names)
    {
        array = default;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Array)
            {
                array = node;
                return true;
            }
        }

        return false;
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() ?? string.Empty : string.Empty;

    private static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var node) && node.TryGetInt32(out var value) ? value : 0;

    private static long ReadLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var node) && node.TryGetInt64(out var value) ? value : 0;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectCallback = IpV4Connector.ConnectAsync };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}

/// <summary>
/// 直播间详情.
/// </summary>
public sealed class LiveRoomDetail
{
    public string RoomId { get; set; } = string.Empty;

    public string ShortRoomId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public bool IsLive { get; set; }

    public int Online { get; set; }

    public string AreaName { get; set; } = string.Empty;

    public string ParentAreaName { get; set; } = string.Empty;

    public long StartTime { get; set; }

    public long AnchorUid { get; set; }

    public string AnchorName { get; set; } = string.Empty;

    public string AnchorAvatar { get; set; } = string.Empty;

    public long FansCount { get; set; }

    public string FansText { get; set; } = string.Empty;
}

/// <summary>
/// 一条可用的直播流（画质 + 线路）.
/// </summary>
public sealed class LiveStreamOption
{
    public string Protocol { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public string Codec { get; set; } = string.Empty;

    public int Quality { get; set; }

    public IReadOnlyList<int> AcceptQualities { get; set; } = Array.Empty<int>();

    public int LineIndex { get; set; }

    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// 礼物榜条目.
/// </summary>
public sealed class LiveRankItem
{
    public int Rank { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Avatar { get; set; } = string.Empty;

    public long Score { get; set; }

    public bool IsGuard { get; set; }
}

/// <summary>
/// 大航海条目.
/// </summary>
public sealed class LiveGuardItem
{
    public string UserName { get; set; } = string.Empty;

    public string Avatar { get; set; } = string.Empty;

    public int GuardLevel { get; set; }

    public long TotalWealth { get; set; }
}

/// <summary>
/// 礼物背包条目.
/// </summary>
public sealed class LiveGiftBagItem
{
    public string GiftName { get; set; } = string.Empty;

    public string GiftId { get; set; } = string.Empty;

    public int Number { get; set; }

    public string CoinType { get; set; } = string.Empty;

    public int Price { get; set; }

    public string Image { get; set; } = string.Empty;

    /// <summary>
    /// 数量文本.
    /// </summary>
    public string NumberText => Number.ToString();
}
