// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 通过解析服务器获取播放地址.
/// 参考 BiliLite：把 playurl 的 API 域名换成代理服务器，并附带 area 参数以解锁区域限制.
/// </summary>
internal static class ProxiedPlayUrlService
{
    private const string DefaultProxyServer = "https://bili.nepnep.moe";
    private const string ApiBase = "https://api.bilibili.com";
    private const string WebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private const string WebReferer = "https://www.bilibili.com";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>
    /// 是否启用播放代理.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.ProxyEnabled, false);

    /// <summary>
    /// 取首选代理服务器.
    /// </summary>
    public static string PreferredServer
    {
        get
        {
            var server = SettingsToolkit.ReadLocalSetting(SettingNames.ProxyServer, DefaultProxyServer);
            return string.IsNullOrWhiteSpace(server) ? DefaultProxyServer : server.TrimEnd('/');
        }
    }

    /// <summary>
    /// 按区域取代理服务器.
    /// </summary>
    public static string GetServerForArea(string area)
        => area switch
        {
            "hk" => FirstNonEmpty(SettingsToolkit.ReadLocalSetting(SettingNames.ProxyServerHk, string.Empty), PreferredServer),
            "tw" => FirstNonEmpty(SettingsToolkit.ReadLocalSetting(SettingNames.ProxyServerTw, string.Empty), PreferredServer),
            "cn" => FirstNonEmpty(SettingsToolkit.ReadLocalSetting(SettingNames.ProxyServerCn, string.Empty), PreferredServer),
            _ => PreferredServer,
        };

    /// <summary>
    /// 通过代理服务器拿视频播放地址.
    /// </summary>
    /// <param name="bvid">视频 BV 号.</param>
    /// <param name="cid">分 P 的 cid.</param>
    /// <param name="quality">清晰度 qn.</param>
    /// <param name="preferCodec">偏好编码.</param>
    /// <param name="cookie">登录 Cookie.</param>
    /// <param name="logger">日志.</param>
    public static async Task<(string? Video, string? Audio)?> GetPlayUrlsAsync(
        string bvid,
        string cid,
        int quality,
        string preferCodec,
        string cookie,
        ILogger? logger = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(bvid) || string.IsNullOrWhiteSpace(cid))
        {
            return null;
        }

        var aid = await GetAidAsync(bvid, cookie, logger);
        if (aid <= 0)
        {
            logger?.LogWarning("代理播放：未能拿到 {Bvid} 的 avid", bvid);
            return null;
        }

        foreach (var area in new[] { string.Empty, "cn", "hk", "tw" })
        {
            var server = string.IsNullOrEmpty(area) ? PreferredServer : GetServerForArea(area);
            var json = await RequestPlayUrlAsync(server, aid, cid, quality, area, cookie, logger);
            if (json is null)
            {
                continue;
            }

            var result = ParseDash(json, quality, preferCodec);
            if (result is not null)
            {
                logger?.LogInformation(
                    "代理播放：{Server}（area={Area}）返回了可用地址",
                    server,
                    string.IsNullOrEmpty(area) ? "无" : area);
                return result;
            }
        }

        return null;
    }

    private static string FirstNonEmpty(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.TrimEnd('/');

    private static async Task<long> GetAidAsync(string bvid, string cookie, ILogger? logger)
    {
        if (!bvid.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(bvid, out var direct) ? direct : 0;
        }

        try
        {
            using var request = CreateRequest($"{ApiBase}/x/web-interface/view?bvid={bvid}", cookie);
            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("aid", out var aidNode))
            {
                return aidNode.GetInt64();
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "代理播放：查询 avid 失败");
        }

        return 0;
    }

    private static async Task<JsonDocument?> RequestPlayUrlAsync(
        string server,
        long aid,
        string cid,
        int quality,
        string area,
        string cookie,
        ILogger? logger)
    {
        try
        {
            var url = $"{server}/x/player/playurl?avid={aid}&cid={cid}&qn={quality}&type=&otype=json&fourk=1&fnver=0&fnval=4048";
            if (!string.IsNullOrEmpty(area))
            {
                url += $"&area={area}";
            }

            using var request = CreateRequest(url, cookie);
            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            var document = await JsonDocument.ParseAsync(stream);
            if (document.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                logger?.LogDebug("代理播放：{Server} 返回 code={Code}", server, code.GetInt32());
                document.Dispose();
                return null;
            }

            return document;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "代理播放：请求 {Server} 失败", server);
            return null;
        }
    }

    private static (string? Video, string? Audio)? ParseDash(JsonDocument document, int quality, string preferCodec)
    {
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("dash", out var dash))
        {
            return null;
        }

        string? video = null;
        string? audio = null;

        if (dash.TryGetProperty("video", out var videos) && videos.ValueKind == JsonValueKind.Array)
        {
            var candidates = new List<(int Quality, string Codecs, string Url)>();
            foreach (var item in videos.EnumerateArray())
            {
                var idText = item.TryGetProperty("id", out var idNode) ? idNode.GetInt32().ToString(CultureInfo.InvariantCulture) : string.Empty;
                var codecs = item.TryGetProperty("codecs", out var codecsNode) ? codecsNode.GetString() ?? string.Empty : string.Empty;
                var url = item.TryGetProperty("baseUrl", out var baseNode)
                    ? baseNode.GetString()
                    : item.TryGetProperty("base_url", out var baseNode2) ? baseNode2.GetString() : null;
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                _ = int.TryParse(idText, out var q);
                candidates.Add((q, codecs, url));
            }

            var selected = candidates.FirstOrDefault(p => p.Quality == quality && p.Codecs.Contains(preferCodec, StringComparison.OrdinalIgnoreCase));
            if (selected.Url is null)
            {
                selected = candidates.FirstOrDefault(p => p.Quality == quality);
            }

            if (selected.Url is null && candidates.Count > 0)
            {
                selected = candidates.OrderByDescending(p => p.Quality).First();
            }

            video = selected.Url;
        }

        if (dash.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array)
        {
            var best = audios
                .EnumerateArray()
                .Select(item => new
                {
                    Id = item.TryGetProperty("id", out var idNode) ? idNode.GetInt32() : 0,
                    Url = item.TryGetProperty("baseUrl", out var baseNode)
                        ? baseNode.GetString()
                        : item.TryGetProperty("base_url", out var baseNode2) ? baseNode2.GetString() : null,
                })
                .Where(p => !string.IsNullOrEmpty(p.Url))
                .OrderByDescending(p => p.Id)
                .FirstOrDefault();

            audio = best?.Url;
        }

        return video is null && audio is null ? null : (video, audio);
    }

    private static HttpRequestMessage CreateRequest(string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", WebReferer);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
    }
}