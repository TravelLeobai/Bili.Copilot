// Copyright (c) Bili Copilot. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// B 站 WBI 签名.
/// 2025 年起部分 Web 接口（含直播间 getDanmuInfo）需要 wts + w_rid 签名与 buvid3。
/// </summary>
internal static class BiliWbiSigner
{
    private static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52,
    ];

    private static readonly HttpClient Client = new(new SocketsHttpHandler { ConnectCallback = IpV4Connector.ConnectAsync }) { Timeout = TimeSpan.FromSeconds(10) };
    private static string _mixinKey = string.Empty;
    private static DateTimeOffset _mixinKeyExpire = DateTimeOffset.MinValue;

    /// <summary>
    /// 生成带签名的查询串.
    /// </summary>
    /// <param name="parameters">业务参数（不含 wts / w_rid）.</param>
    /// <param name="cookie">Cookie.</param>
    /// <param name="logger">日志.</param>
    public static async Task<string> SignAsync(IDictionary<string, string> parameters, string cookie, ILogger? logger = null)
    {
        var mixinKey = await GetMixinKeyAsync(cookie, logger).ConfigureAwait(false);
        var wts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var all = new Dictionary<string, string>(parameters) { ["wts"] = wts };

        var query = string.Join(
            "&",
            all.OrderBy(p => p.Key, StringComparer.Ordinal)
               .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(Filter(p.Value))}"));

        if (string.IsNullOrEmpty(mixinKey))
        {
            return $"{query}&wts={wts}";
        }

        var wRid = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey)));
        return $"{query}&w_rid={wRid}";
    }

    /// <summary>
    /// 取 buvid3；Cookie 里没有就向 spi 接口要一个.
    /// </summary>
    public static async Task<string> GetBuvid3Async(string cookie, ILogger? logger = null)
    {
        var existing = ReadCookie(cookie, "buvid3");
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/frontend/finger/spi");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("b_3", out var b3))
            {
                return b3.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "获取 buvid3 失败");
        }

        return string.Empty;
    }

    private static string Filter(string value)
        => string.Concat(value.Where(c => "!'()*".IndexOf(c) < 0));

    /// <summary>
    /// 一次取回 buvid3 与 buvid4（同一个 spi 接口本来就一起给）.
    /// cookie 里已有的优先用已有的.
    /// </summary>
    public static async Task<(string Buvid3, string Buvid4)> GetBuvidPairAsync(string cookie, ILogger? logger = null)
    {
        var existing3 = ReadCookie(cookie, "buvid3");
        var existing4 = ReadCookie(cookie, "buvid4");
        if (!string.IsNullOrEmpty(existing3) && !string.IsNullOrEmpty(existing4))
        {
            return (existing3, existing4);
        }

        var b3 = existing3;
        var b4 = existing4;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/frontend/finger/spi");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data))
            {
                if (string.IsNullOrEmpty(b3) && data.TryGetProperty("b_3", out var node3))
                {
                    b3 = node3.GetString() ?? string.Empty;
                }

                if (string.IsNullOrEmpty(b4) && data.TryGetProperty("b_4", out var node4))
                {
                    b4 = node4.GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "获取 buvid 失败");
        }

        return (b3, b4);
    }

    private static async Task<string> GetMixinKeyAsync(string cookie, ILogger? logger)
    {
        if (!string.IsNullOrEmpty(_mixinKey) && _mixinKeyExpire > DateTimeOffset.Now)
        {
            return _mixinKey;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("wbi_img", out var wbi))
            {
                return string.Empty;
            }

            var imgKey = ExtractKey(wbi, "img_url");
            var subKey = ExtractKey(wbi, "sub_url");
            if (string.IsNullOrEmpty(imgKey) || string.IsNullOrEmpty(subKey))
            {
                return string.Empty;
            }

            var raw = imgKey + subKey;
            var builder = new StringBuilder();
            foreach (var index in MixinKeyEncTab)
            {
                if (index < raw.Length)
                {
                    builder.Append(raw[index]);
                }
            }

            var mixin = builder.ToString();
            _mixinKey = mixin.Length > 32 ? mixin[..32] : mixin;
            _mixinKeyExpire = DateTimeOffset.Now.AddHours(2);
            return _mixinKey;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "获取 WBI 密钥失败");
            return string.Empty;
        }
    }

    private static string ExtractKey(JsonElement wbi, string name)
    {
        if (!wbi.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var url = node.GetString() ?? string.Empty;
        var file = url.Split('/').LastOrDefault() ?? string.Empty;
        var dot = file.IndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    private static string ReadCookie(string cookie, string name)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0 && part[..index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(index + 1)..];
            }
        }

        return string.Empty;
    }
}