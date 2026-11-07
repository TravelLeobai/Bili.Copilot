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
    public const string DefaultHomePage = "http://mxd.indevs.in/hd2a";

    /// <summary>
    /// 默认用户脚本地址（上游提供的最小化脚本）.
    /// </summary>
    public const string DefaultScriptUrl = "http://mxd.indevs.in/hd2a/assets/min.baidu.user.js";

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
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultHomePage;
            }

            // hd2a.page.gd 已经不作数了，老配置里存着它就自动迁到新主页，
            // 否则每次同步都先卡在死域名上.
            var trimmed = value.TrimEnd('/');
            if (trimmed.Equals("https://hd2a.page.gd", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("http://hd2a.page.gd", StringComparison.OrdinalIgnoreCase))
            {
                return DefaultHomePage;
            }

            // 不要自作聪明把 http 升级成 https：mxd.indevs.in 只有 http 能用，
            // 走 https 会卡在 TLS 握手直到超时，整条解锁链路被判失败、回退常规地址.
            return trimmed;
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
        // 老配置里存的可能还是已经作废的 hd2a.page.gd，所以这里多准备几个候选地址：
        // 当前主页 → 解析服务器下的新路径 → 解析服务器根目录.
        foreach (var homeTxt in new[]
                 {
                     $"{homePage}/assets/home.txt",
                     $"{Server}/hd2a/assets/home.txt",
                     $"{Server}/assets/home.txt",
                 })
        {
            if (!string.IsNullOrEmpty(result.Server))
            {
                break;
            }

            try
            {
                using var request = CreateRequest(homeTxt);
                using var response = await Client.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var text = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
                if (!text.StartsWith('{'))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("home", out var home)
                    && home.GetString() is { Length: > 0 } server)
                {
                    result.Server = server;
                    result.Source = homeTxt;
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "读取 home.txt 失败：{Url}", homeTxt);
            }
        }

        // 2) 抓上游用户脚本，解析 box.home 字段.
        if (string.IsNullOrEmpty(result.Server))
        {
            try
            {
                // 脚本地址也跟着换过位置：先试 {Server}/hd2a/assets/…，再试旧路径.
                var script = await TryFetchScriptAsync($"{Server}/hd2a/assets/min.baidu.user.js").ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(script))
                {
                    script = await TryFetchScriptAsync(DefaultScriptUrl).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(script))
                {
                    script = await TryFetchScriptAsync($"{homePage}/assets/tampermonkey.user.js").ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(script))
                {
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
        // 解锁服务器是拿这个 URL 自己去 B 站取 playurl 的，
        // 所以必须和浏览器发出的一样带 wbi 签名（wts + w_rid）；裸 URL 会被 B 站判风险，
        // 服务端取不到东西就回 {"code":2,"message":"fail"}——这就是「自动最高画质不起效」的直接原因.
        var playQuery = await BiliWbiSigner.SignAsync(
            new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid,
                ["qn"] = qn.ToString(),
                ["type"] = "0",
                ["otype"] = "json",
                ["fourk"] = "1",
                ["fnver"] = "0",
                ["fnval"] = "4048",
                ["platform"] = "pc",
                ["high_quality"] = "1",
            },
            cookie,
            logger).ConfigureAwait(false);
        var playUrl = $"https://api.bilibili.com/x/player/wbi/playurl?{playQuery}";

        // 登录态一律转发：解锁服务器要靠它拿到大会员画质.
        // buvid3 缺失时 B 站会对 playurl 走风控，服务端替我们取就失败（表现为 code=2 fail），
        // 所以先看看本地有没有，没有就要一个再一起转发.
        var cookieForServer = cookie;
        try
        {
            if (!cookieForServer.Contains("buvid3=", StringComparison.Ordinal))
            {
                var buvid3 = await BiliWbiSigner.GetBuvid3Async(cookie, logger).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(buvid3))
                {
                    cookieForServer = string.IsNullOrEmpty(cookieForServer)
                        ? $"buvid3={buvid3}"
                        : $"{cookieForServer.TrimEnd(';')}; buvid3={buvid3}";
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "取 buvid3 失败，按原 cookie 转发");
        }

        // 服务端要的 ui 结构是照脚本来的：ua/mid/vip/level/money/cookie/csrf/name/face.
        // 少了 name、face 会被判成 invalid（实测返回 {"code":2,"message":"invalid"}）.
        var (name, face, vip, level) = await ReadAccountProfileAsync(cookie, mid, logger).ConfigureAwait(false);
        logger?.LogInformation(
            "画质解锁 ui 字段：mid={Mid} name={Name} face={HasFace} vip={Vip} level={Level}",
            mid,
            string.IsNullOrEmpty(name) ? "空" : name,
            string.IsNullOrEmpty(face) ? "无" : "有",
            vip,
            level);
        var ui = $"{{\"ua\":\"{Escape(UserAgent)}\",\"mid\":\"{Escape(mid)}\","
            + $"\"vip\":{(vip ? 1 : 0)},\"level\":{level},\"money\":0,"
            + $"\"cookie\":\"{Escape(cookieForServer)}\",\"csrf\":\"{Escape(csrf)}\","
            + $"\"name\":\"{Escape(name)}\",\"face\":\"{Escape(face)}\"}}";
        var body = $"{{\"ui\":{ui},\"url\":\"{Escape(playUrl)}\"}}";

        try
        {
            await RegisterAsync(ui, logger).ConfigureAwait(false);
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
                if (code == 9)
                {
                    // 脚本里 code 9 是「次数已达上限」，提示投币重置额度.
                    message = "解锁次数已达上限，给任意视频投币可以重置额度";
                }

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

    /// <summary>
    /// 读一份当前账号的资料：解锁服务器的 ui 需要昵称、头像、会员状态和等级.
    /// 拿不到就退回空串，让服务端自己去判断.
    /// </summary>
    private static async Task<(string Name, string Face, bool Vip, int Level)> ReadAccountProfileAsync(
        string cookie,
        string mid,
        ILogger? logger)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (string.Empty, string.Empty, false, 2);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, string.Empty, false, 2);
            }

            var name = data.TryGetProperty("uname", out var uname) ? uname.GetString() ?? string.Empty : string.Empty;
            var face = data.TryGetProperty("face", out var faceNode) ? faceNode.GetString() ?? string.Empty : string.Empty;
            var vip = data.TryGetProperty("vipStatus", out var vipNode) && vipNode.TryGetInt32(out var vipStatus) && vipStatus != 0;
            var level = 2;
            if (data.TryGetProperty("level_info", out var levelInfo)
                && levelInfo.ValueKind == JsonValueKind.Object
                && levelInfo.TryGetProperty("current_level", out var currentLevel)
                && currentLevel.TryGetInt32(out var currentLevelValue))
            {
                level = currentLevelValue;
            }

            return (name, face, vip, level);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "读取账号资料失败，按游客身份继续");
            return (string.Empty, string.Empty, false, 2);
        }
    }

    /// <summary>
    /// 向解锁服务器登记一次身份（脚本初始化时也会调 /api/bzusta）.
    /// 失败不影响后续请求，服务端会自己去取.
    /// </summary>
    private static async Task RegisterAsync(string ui, ILogger? logger)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Server}/api/bzusta")
            {
                Content = new StringContent($"{{\"ui\":{ui}}}", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Origin", Server);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            logger?.LogInformation("画质解锁服务器登记：HTTP {Status} {Body}", (int)response.StatusCode, text.Length > 200 ? text[..200] : text);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "画质解锁服务器登记失败，继续尝试直接解锁");
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

    /// <summary>
    /// 取一个脚本地址的内容，失败返回空串.
    /// </summary>
    /// <param name="url">脚本地址.</param>
    /// <returns>脚本文本.</returns>
    private static async Task<string> TryFetchScriptAsync(string url)
    {
        try
        {
            using var request = CreateRequest(url);
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
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






