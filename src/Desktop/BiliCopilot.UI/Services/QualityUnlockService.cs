// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 画质解锁（第三方解析服务器）.
/// 参考「网盘下载助手」油猴脚本的做法：不转发地址，而是把 playurl 请求整体交给
/// 第三方服务器（脚本里的 box.home）代取，由它用高等级账号返回 dash 地址。
/// 服务器地址每天从上游脚本 / assets/home.txt 同步一次，用户也可以手填.
/// </summary>
internal static class QualityUnlockService
{
    /// <summary>
    /// 默认服务器（脚本 v0.0.6 里的 box.home）.
    /// </summary>
    public const string DefaultServer = "http://mxd.indevs.in";

    /// <summary>
    /// 默认上游主页（脚本的 @homepage，assets/home.txt 也挂在这里）.
    /// </summary>
    public const string DefaultHomePage = "http://hd2a.page.gd";

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = IpV4Connector.ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    /// <summary>
    /// 是否启用画质解锁.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.QualityUnlockEnabled, false);

    /// <summary>
    /// 是否每次进入视频自动选最高画质.
    /// </summary>
    public static bool AutoMaxQuality
        => SettingsToolkit.ReadLocalSetting(SettingNames.QualityUnlockAutoMax, true);

    /// <summary>
    /// 是否把登录态一起发给解锁服务器（优先关闭）.
    /// </summary>
    public static bool SendCookie
        => SettingsToolkit.ReadLocalSetting(SettingNames.QualityUnlockSendCookie, false);

    /// <summary>
    /// 当前使用的解析服务器.
    /// </summary>
    public static string Server
    {
        get
        {
            var value = SettingsToolkit.ReadLocalSetting(SettingNames.QualityUnlockServer, DefaultServer);
            return string.IsNullOrWhiteSpace(value) ? DefaultServer : value.TrimEnd('/');
        }
    }

    /// <summary>
    /// 当前上游主页.
    /// </summary>
    public static string HomePage
    {
        get
        {
            var value = SettingsToolkit.ReadLocalSetting(SettingNames.QualityUnlockHomePage, DefaultHomePage);
            return string.IsNullOrWhiteSpace(value) ? DefaultHomePage : value.TrimEnd('/');
        }
    }

    /// <summary>
    /// 上次同步时间.
    /// </summary>
    public static DateTimeOffset LastSync
        => DateTimeOffset.FromUnixTimeSeconds(SettingsToolkit.ReadLocalSetting(SettingNames.QualityUnlockLastSync, 0L)).ToLocalTime();

    /// <summary>
    /// 是否需要同步（距上次超过 24 小时）.
    /// </summary>
    public static bool NeedSync => DateTimeOffset.Now - LastSync > TimeSpan.FromHours(24);

    /// <summary>
    /// 从上游同步服务器地址.
    /// 依次尝试：<c>{homepage}/assets/home.txt</c>（脚本自己用的重定向文件）→ 上游用户脚本里解析 <c>home:</c> 字段.
    /// </summary>
    /// <param name="logger">日志.</param>
    public static async Task<QualityUnlockSyncResult> SyncAsync(ILogger? logger = null)
    {
        var result = new QualityUnlockSyncResult();
        var homePage = HomePage;

        // 1) 脚本启动时会读这个文件，返回 {"home":"http://..."} 表示服务器搬到了哪.
        try
        {
            using var request = CreateRequest($"{homePage}/assets/home.txt");
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var text = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
                if (text.StartsWith('{'))
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.TryGetProperty("home", out var home)
                        && home.GetString() is { Length: > 0 } server)
                    {
                        result.Server = server;
                        result.Source = "assets/home.txt";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "读取 assets/home.txt 失败");
        }

        // 2) 抓上游用户脚本，解析 box.home 字段.
        if (string.IsNullOrEmpty(result.Server))
        {
            try
            {
                var scriptUrl = SettingsToolkit.ReadLocalSetting(SettingNames.QualityUnlockScriptUrl, string.Empty);
                if (string.IsNullOrWhiteSpace(scriptUrl))
                {
                    scriptUrl = $"{homePage}/assets/tampermonkey.user.js";
                }

                using var request = CreateRequest(scriptUrl);
                using var response = await Client.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var script = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var match = Regex.Match(script, @"home\s*:\s*[""'](https?://[^""']+)[""']");
                    if (match.Success)
                    {
                        result.Server = match.Groups[1].Value;
                        result.Source = "用户脚本 home 字段";
                    }

                    var pageMatch = Regex.Match(script, @"@homepage\s+(https?://\S+)");
                    if (pageMatch.Success && !string.Equals(pageMatch.Groups[1].Value, homePage, StringComparison.OrdinalIgnoreCase))
                    {
                        SettingsToolkit.WriteLocalSetting(SettingNames.QualityUnlockHomePage, pageMatch.Groups[1].Value);
                        result.HomePage = pageMatch.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "解析上游脚本失败");
            }
        }

        SettingsToolkit.WriteLocalSetting(SettingNames.QualityUnlockLastSync, DateTimeOffset.Now.ToUnixTimeSeconds());

        if (!string.IsNullOrEmpty(result.Server))
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.QualityUnlockServer, result.Server);
            logger?.LogInformation("画质解锁服务器已同步为 {Server}（来源：{Source}）", result.Server, result.Source);
        }
        else
        {
            logger?.LogWarning("画质解锁服务器同步失败，继续使用 {Server}", Server);
        }

        return result;
    }

    /// <summary>
    /// 通过解锁服务器取播放地址.
    /// 请求体与脚本一致：<c>{ ui, url }</c>，url 为原本的 playurl 请求.
    /// </summary>
    /// <param name="bvid">视频 bvid.</param>
    /// <param name="cid">分 P cid.</param>
    /// <param name="qn">请求画质.</param>
    /// <param name="cookie">本机 cookie.</param>
    /// <param name="csrf">bili_jct.</param>
    /// <param name="mid">当前用户 mid.</param>
    /// <param name="logger">日志.</param>
    public static async Task<QualityUnlockResult> GetPlayUrlsAsync(
        string bvid,
        string cid,
        int qn,
        string cookie,
        string csrf,
        string mid,
        ILogger? logger = null)
    {
        var playUrl = $"https://api.bilibili.com/x/player/wbi/playurl?bvid={bvid}&cid={cid}&qn={qn}"
            + "&type=0&otype=json&fourk=1&fnver=0&fnval=4048&platform=pc&high_quality=1";

        var cookieForServer = SendCookie ? cookie : string.Empty;
        var ui = $"{{\"ua\":\"{Escape(UserAgent)}\",\"mid\":\"{Escape(mid)}\",\"vip\":0,\"level\":2,\"money\":0,"
            + $"\"cookie\":\"{Escape(cookieForServer)}\",\"csrf\":\"{Escape(csrf)}\"}}";
        var body = $"{{\"ui\":{ui},\"url\":\"{Escape(playUrl)}\"}}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Server}/api/bzview2")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Origin", Server);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("画质解锁请求失败：HTTP {Status}", (int)response.StatusCode);
                return QualityUnlockResult.Failed($"HTTP {(int)response.StatusCode}");
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var codeNode) && codeNode.TryGetInt32(out var code) && code != 0)
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? string.Empty : string.Empty;
                logger?.LogWarning("画质解锁被拒绝：code={Code} message={Message}", code, message);
                return QualityUnlockResult.Failed(message);
            }

            var (video, audio, quality) = ParseDash(root);
            if (string.IsNullOrEmpty(video) && string.IsNullOrEmpty(audio))
            {
                logger?.LogWarning("画质解锁返回里没有 dash 地址");
                return QualityUnlockResult.Failed("返回数据里没有可用地址");
            }

            logger.LogInformation("画质解锁成功：qn={Quality}", quality);
            return new QualityUnlockResult
            {
                Success = true,
                Video = video,
                Audio = audio,
                Quality = quality,
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "画质解锁请求异常");
            return QualityUnlockResult.Failed(ex.Message);
        }
    }

    private static (string? Video, string? Audio, int Quality) ParseDash(JsonElement root)
    {
        // 服务器直接把 playurl 的 JSON 返回回来，结构同 B 站：data.dash.video[] / data.dash.audio[].
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            root = data;
        }

        if (!root.TryGetProperty("dash", out var dash) || dash.ValueKind != JsonValueKind.Object)
        {
            return (null, null, 0);
        }

        var quality = dash.TryGetProperty("video", out var videos) && videos.ValueKind == JsonValueKind.Array
            ? videos.EnumerateArray().Select(p => p.TryGetProperty("id", out var id) ? id.GetInt32() : 0).DefaultIfEmpty(0).Max()
            : 0;

        string? video = null;
        if (dash.TryGetProperty("video", out var videoArray) && videoArray.ValueKind == JsonValueKind.Array)
        {
            video = videoArray.EnumerateArray()
                .OrderByDescending(p => p.TryGetProperty("id", out var id) ? id.GetInt32() : 0)
                .Select(p => p.TryGetProperty("baseUrl", out var url) ? url.GetString()
                    : p.TryGetProperty("base_url", out var url2) ? url2.GetString() : null)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        }

        string? audio = null;
        if (dash.TryGetProperty("audio", out var audioArray) && audioArray.ValueKind == JsonValueKind.Array)
        {
            audio = audioArray.EnumerateArray()
                .OrderByDescending(p => p.TryGetProperty("bandwidth", out var bw) ? bw.GetInt64() : 0)
                .Select(p => p.TryGetProperty("baseUrl", out var url) ? url.GetString()
                    : p.TryGetProperty("base_url", out var url2) ? url2.GetString() : null)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        }

        return (video, audio, quality);
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", HomePage);
        return request;
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
}

/// <summary>
/// 画质解锁结果.
/// </summary>
internal sealed class QualityUnlockResult
{
    public bool Success { get; init; }

    public string? Video { get; init; }

    public string? Audio { get; init; }

    public int Quality { get; init; }

    public string? Message { get; init; }

    public static QualityUnlockResult Failed(string message)
        => new() { Success = false, Message = message };
}

/// <summary>
/// 服务器同步结果.
/// </summary>
internal sealed class QualityUnlockSyncResult
{
    public string? Server { get; set; }

    public string? HomePage { get; set; }

    public string? Source { get; set; }

    public bool Success => !string.IsNullOrEmpty(Server);
}