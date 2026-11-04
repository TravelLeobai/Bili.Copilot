// Copyright (c) Bili Copilot. All rights reserved.

using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 直播弹幕 / 表情发送与表情列表，实现思路来自 biliuwp-lite：
/// 1. 文字：POST /api/sendmsg（App 接口），body 参数按字典序排好拼 appsecret 做 MD5 签名；
/// 2. 表情：POST /xlive/app-room/v1/dM/sendmsg，msg 传 emoticon_unique，
///    extra 里带 content / emoticon_unique / dm_type=1（biliuwp-lite 的 ios 端做法）；
/// 3. 表情列表：GET /xlive/web-ucenter/v2/emoticon/GetEmoticons。
/// </summary>
public static class LiveRoomSendApi
{
    private const string LiveApi = "https://api.live.bilibili.com";
    private const string AppKey = "1d8b6e7d45233436";
    private const string AppSecret = "560c52ccd288fed045859ed18bffd973";
    private const string AndroidBuild = "7840300";
    private const string AndroidUa = "Mozilla/5.0 BiliDroid/7.84.0 (bbcallen@gmail.com) os/android model/Pixel mobi_app/android build/7840300 channel/master innerVer/7840300 osVer/13 network/2";

    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// 签名：参数按字典序排序后拼 appsecret，取 MD5 小写.
    /// </summary>
    /// <param name="parameters">k=v&amp;k=v 形式.</param>
    public static string Sign(string parameters)
    {
        var ordered = parameters
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var raw = string.Join("&", ordered) + AppSecret;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 发送文字弹幕.
    /// </summary>
    public static async Task<(bool Ok, int Code, string Message)> SendTextAsync(
        string roomId,
        string text,
        long uid,
        string cookie,
        ILogger? logger = null)
    {
        var rnd = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var csrf = ExtractCsrf(cookie);
        var body = string.Join("&", new[]
        {
            $"cid={roomId}",
            $"mid={uid}",
            $"msg={WebUtility.UrlEncode(text)}",
            $"rnd={rnd}",
            "mode=1",
            "pool=0",
            "type=json",
            "color=16777215",
            "fontsize=25",
            "playTime=0.0",
            $"csrf={csrf}",
        });

        var url = $"{LiveApi}/api/sendmsg?{AppQuery()}&actionKey=appkey&sign={Sign(body)}";
        return await PostAsync(url, body, cookie, "app/sendmsg", logger).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送表情弹幕：先用网页端接口发表情文本（[doge] 这种，B 站会自己渲染成图），
    /// 失败再走 App 的 dM/sendmsg（大表情专用，需要 access_key）.
    /// </summary>
    public static async Task<(bool Ok, int Code, string Message)> SendEmoteAsync(
        string roomId,
        LiveEmoteItem emote,
        long uid,
        string cookie,
        ILogger? logger = null)
    {
        // 1) 先走 App 的 dM/sendmsg：这条会把 emoticon_unique 一起提交，房间里能显示成图.
        var ts = DateTimeOffset.Now.ToUnixTimeSeconds();
        var rnd = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var extra = BuildEmoteExtra(emote);
        var body = string.Join("&", new[]
        {
            $"msg={WebUtility.UrlEncode(emote.Unique)}",
            "dm_type=1",
            $"extra={WebUtility.UrlEncode(extra)}",
            $"cid={roomId}",
            $"mid={uid}",
            $"ts={ts}",
            $"rnd={rnd}",
            "mode=1",
            "pool=0",
            "type=json",
            "color=16777215",
            "fontsize=25",
            $"csrf={ExtractCsrf(cookie)}",
        });

        var url = $"{LiveApi}/xlive/app-room/v1/dM/sendmsg?{AppQuery()}&actionKey=appkey&sign={Sign(body)}";
        var appResult = await PostAsync(url, body, cookie, "app/dM/sendmsg", logger).ConfigureAwait(false);
        if (appResult.Ok)
        {
            return appResult;
        }

        // 2) App 接口不行就发文本 token（[doge] 这种 B 站会自己渲染成表情）.
        logger?.LogInformation("App 表情接口失败（code={Code} {Message}），退回文本方式", appResult.Code, appResult.Message);
        if (!string.IsNullOrWhiteSpace(emote.Text))
        {
            var textResult = await LiveRoomApi.SendDanmakuAsync(roomId, emote.Text, cookie, ExtractCsrf(cookie), logger).ConfigureAwait(false);
            if (textResult)
            {
                logger?.LogInformation("表情按文本发送成功：{Text}", emote.Text);
                return (true, 0, string.Empty);
            }
        }

        return appResult;
    }

    /// <summary>
    /// 取直播间表情列表.
    /// </summary>
    public static async Task<IReadOnlyList<LiveEmoteItem>> GetEmoticonsAsync(string roomId, string cookie, ILogger? logger = null)
    {
        var candidates = new[]
        {
            $"{LiveApi}/xlive/web-ucenter/v2/emoticon/GetEmoticons?platform=pc&room_id={WebUtility.UrlEncode(roomId)}",
            $"{LiveApi}/xlive/web-ucenter/v2/emoticon/GetEmoticons?room_id={WebUtility.UrlEncode(roomId)}",
            $"{LiveApi}/xlive/app-ucenter/v2/emoticon/GetEmoticons?room_id={WebUtility.UrlEncode(roomId)}",
        };

        foreach (var candidate in candidates)
        {
            var items = await TryGetEmoticonsAsync(candidate, cookie, logger).ConfigureAwait(false);
            if (items.Count > 0)
            {
                return items;
            }
        }

        return Array.Empty<LiveEmoteItem>();
    }

    private static async Task<IReadOnlyList<LiveEmoteItem>> TryGetEmoticonsAsync(string url, string cookie, ILogger? logger)
    {
        var result = new List<LiveEmoteItem>();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", AndroidUa);
            request.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("data", out var packages) ||
                packages.ValueKind != JsonValueKind.Array)
            {
                logger?.LogWarning("直播间表情接口没有返回数据：{Body}", json.Length > 400 ? json[..400] : json);
                return result;
            }

            foreach (var package in packages.EnumerateArray())
            {
                if (!package.TryGetProperty("emoticons", out var emoticons) || emoticons.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var emote in emoticons.EnumerateArray())
                {
                    var unique = emote.TryGetProperty("emoticon_unique", out var u) ? u.GetString() : null;
                    var emoteUrl = emote.TryGetProperty("url", out var ur) ? ur.GetString() : null;
                    if (string.IsNullOrWhiteSpace(unique) || string.IsNullOrWhiteSpace(emoteUrl))
                    {
                        continue;
                    }

                    if (emoteUrl.StartsWith("//", StringComparison.Ordinal))
                    {
                        emoteUrl = "https:" + emoteUrl;
                    }

                    result.Add(new LiveEmoteItem
                    {
                        Unique = unique,
                        Text = emote.TryGetProperty("emoji", out var e) ? e.GetString() ?? string.Empty : string.Empty,
                        Url = emoteUrl,
                        IsLocked = emote.TryGetProperty("perm", out var p) && p.GetInt32() == 0,
                    });
                }
            }

            if (result.Count == 0)
            {
                logger?.LogWarning("直播间表情为空，接口原文前 400 字：{Body}", json.Length > 400 ? json[..400] : json);
            }

            logger?.LogInformation("直播间表情加载完成：{Count} 个（{Url}）", result.Count, url);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "获取直播间表情失败");
        }

        return result;
    }

    private static string BuildEmoteExtra(LiveEmoteItem emote)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("content", emote.Text);
            writer.WriteString("emoticon_unique", emote.Unique);
            writer.WriteNumber("dm_type", 1);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string AppQuery()
    {
        var ts = DateTimeOffset.Now.ToUnixTimeSeconds();
        return $"appkey={AppKey}&build={AndroidBuild}&mobi_app=android&platform=android&ts={ts}";
    }

    private static async Task<(bool Ok, int Code, string Message)> PostAsync(
        string url,
        string body,
        string cookie,
        string tag,
        ILogger? logger)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("User-Agent", AndroidUa);
            request.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var (code, message) = ParseCodeMessage(content, logger);
            logger?.LogInformation("直播弹幕（{Tag}）返回：HTTP {Status} code={Code} message={Message}", tag, (int)response.StatusCode, code, message);
            return (code == 0, code, message);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "直播弹幕（{Tag}）请求异常", tag);
            return (false, -1, ex.Message);
        }
    }

    private static (int Code, string Message) ParseCodeMessage(string content, ILogger? logger)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var code = document.RootElement.TryGetProperty("code", out var codeNode) ? codeNode.GetInt32() : -1;
            var message = document.RootElement.TryGetProperty("message", out var msgNode) ? msgNode.GetString() ?? string.Empty : string.Empty;
            return (code, message);
        }
        catch (JsonException)
        {
            logger?.LogWarning("直播弹幕返回不是 JSON：{Body}", content.Length > 200 ? content[..200] : content);
            return (-1, "not-json");
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return client;
    }
    private static string ExtractCsrf(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return string.Empty;
        }

        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("bili_jct=", StringComparison.OrdinalIgnoreCase))
            {
                return part["bili_jct=".Length..];
            }
        }

        return string.Empty;
    }
}
