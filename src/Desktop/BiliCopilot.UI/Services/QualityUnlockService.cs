// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
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
        // 解析服务器要拿 cookie 自己去 B 站取 playurl，实测一次 12~21 秒，
        // 原来给 12 秒会卡在半路（表现为 TaskCanceledException / Timeout of 12 seconds）.
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// 是否启用画质解锁.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.QualityUnlockEnabled, true);

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
        // 解析服务的接口说明里 url.query 是 { avid, bvid, cid, fn }，浏览器那条请求本来也带着
        // avid 和 fn，缺了容易被它判成"不是浏览器的请求"，所以先查 aid 再一起签名。
        // 参数集必须和浏览器播放器发出的那条一模一样（抓包实录：qz 112 那次请求的形态）：
        // avid,bvid,cid,qn,fnver,fnval,fourk,from_client=BROWSER,is_main_page=true
        // 少一个或多一个都会被服务端判成「不是浏览器请求」，回 {"code":2,"message":"fail"}。
        // 调用方传进来的可能是 avid（纯数字或 av123），而 playurl 需要真正的 BV 号——
        // 之前直接把 avid 塞进 bvid 参数，服务端上游必然 fail（实测日志里 bvid=117585433659167）.
        bvid = await NormalizeBvidAsync(bvid, cookie, logger).ConfigureAwait(false);
        var aid = await GetAidAsync(bvid, cookie, logger).ConfigureAwait(false);
        // 实测：带我们自己签的 w_rid 的 URL 会被服务端上游判成 fail；
        // 去掉 wts/w_rid 的裸 URL（参数不变）返回 code=0 —— 服务端自己补签名.
        // 按需求：signature 一起发。参数集是浏览器抓包那条（qb 112 的形态），
        // 只在这几个字段上签，避免多余参数把 w_rid 算错.
        var playQuery = await BiliWbiSigner.SignAsync(
            new Dictionary<string, string>
            {
                ["avid"] = aid > 0 ? aid.ToString(CultureInfo.InvariantCulture) : "0",
                ["bvid"] = bvid,
                ["cid"] = cid,
                ["qn"] = qn.ToString(CultureInfo.InvariantCulture),
                ["fnver"] = "0",
                ["fnval"] = "4048",
                ["fourk"] = "1",
                ["from_client"] = "BROWSER",
                ["is_main_page"] = "true",
            },
            cookie,
            logger).ConfigureAwait(false);
        var playUrl = $"https://api.bilibili.com/x/player/wbi/playurl?{playQuery}";

        // 登录态一律转发：解锁服务器要靠它拿到大会员画质.
        // buvid3 缺失时 B 站会对 playurl 走风控，服务端替我们取就失败（表现为 code=2 fail），
        // 所以先看看本地有没有，没有就要一个再一起转发.
        var cookieForServer = await PrepareCookieForServerAsync(cookie, logger).ConfigureAwait(false);

        // 服务端要的 ui 结构是照脚本来的：ua/mid/vip/level/money/cookie/csrf/name/face.
        // 少了 name、face 会被判成 invalid（实测返回 {"code":2,"message":"invalid"}）.
        var ui = await BuildUiAsync(cookieForServer, csrf, mid, logger).ConfigureAwait(false);
        var body = $"{{\"ui\":{ui},\"url\":\"{Escape(playUrl)}\"}}";

        // 只记字段名，不记值：日志有可能被贴出去，凭证不能跟着走.
        logger?.LogInformation(
            "画质解锁转发的 cookie 字段：{Keys}",
            string.Join(
                ",",
                cookieForServer.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('=')[0].Trim())
                    .Where(p => p.Length > 0)));

        // 失败时面板要能说清卡在哪，先把这次请求的"环境快照"备好.
        // vip / level 在 BuildUiAsync 里读，这里只记请求侧能看到的.
        var context = $"服务器={Server} qn={qn} avid={(aid > 0 ? aid.ToString(CultureInfo.InvariantCulture) : "未知")} "
            + $"wbi签名={(playQuery.Contains("w_rid=", StringComparison.Ordinal) ? "有" : "无")} "
            + $"buvid3={(cookieForServer.Contains("buvid3=", StringComparison.Ordinal) ? "有" : "无")} "
            + $"mid={mid}";

        // 把请求体落一份（cookie 打码）：出问题时要能和命令行成功的请求逐字段比.
        var maskedBody = body.Replace(cookieForServer, $"(cookie {cookieForServer.Length} 字符)", StringComparison.Ordinal);
        logger?.LogInformation("画质解锁请求体：{Body}", maskedBody);
        logger?.LogInformation("画质解锁请求 URL：{Url}", playUrl);

        return await SendToServerAsync("api/bzview2", ui, body, context, logger).ConfigureAwait(false);
    }

    /// <summary>
    /// 番剧 / 影视走另一条接口：<c>api/bzview3</c>，body 里的 url 换成 ogv 的 playview.
    /// 解析服务的说明里这条要求 <c>url.query = { ep_id, season_id, cid, qn }</c>，少一个都可能判非法.
    /// </summary>
    /// <param name="epId">剧集 ep_id.</param>
    /// <param name="seasonId">番剧 season_id，可为空.</param>
    /// <param name="cid">分 P cid.</param>
    /// <param name="qn">请求画质.</param>
    /// <param name="cookie">本机 cookie.</param>
    /// <param name="csrf">bili_jct.</param>
    /// <param name="mid">当前用户 mid.</param>
    /// <param name="logger">日志.</param>
    public static async Task<QualityUnlockResult> GetPgcPlayUrlsAsync(
        string epId,
        string seasonId,
        string cid,
        int qn,
        string cookie,
        string csrf,
        string mid,
        ILogger? logger = null)
    {
        var cookieForServer = await PrepareCookieForServerAsync(cookie, logger).ConfigureAwait(false);
        var ui = await BuildUiAsync(cookieForServer, csrf, mid, logger).ConfigureAwait(false);

        var query = new List<string>
        {
            $"ep_id={Uri.EscapeDataString(epId ?? string.Empty)}",
            $"season_id={Uri.EscapeDataString(seasonId ?? string.Empty)}",
            $"cid={Uri.EscapeDataString(cid ?? string.Empty)}",
            $"qn={qn.ToString(CultureInfo.InvariantCulture)}",
        };
        var targetUrl = $"https://api.bilibili.com/ogv/player/playview?{string.Join("&", query)}";
        var body = $"{{\"ui\":{ui},\"url\":\"{Escape(targetUrl)}\"}}";
        var context = $"服务器={Server} ep={epId} season={seasonId} cid={cid} qn={qn}";

        logger?.LogInformation("画质解锁（番剧）请求：{Context}", context);
        return await SendToServerAsync("api/bzview3", ui, body, context, logger).ConfigureAwait(false);
    }

    /// <summary>
    /// 补齐转发给服务器的 cookie：buvid3/buvid4 与 _uuid/b_nut 是 B 站的风控字段，
    /// 客户端本地只有很少几个字段，缺了服务端替我们取 playurl 就会被判风控.
    /// </summary>
    private static async Task<string> PrepareCookieForServerAsync(string cookie, ILogger? logger)
    {
        var cookieForServer = cookie;
        try
        {
            // 按需求补 buvid3：B 站对 playurl 会看这个字段，缺了容易走风控.
            var (buvid3, _) = await BiliWbiSigner.GetBuvidPairAsync(cookie, logger).ConfigureAwait(false);
            cookieForServer = AppendCookieValue(cookieForServer, "buvid3", buvid3);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "补 buvid3 失败，按原 cookie 转发");
        }

        return cookieForServer;
    }
    private static async Task<string> BuildUiAsync(
        string cookieForServer,
        string csrf,
        string mid,
        ILogger? logger)
    {
        var (name, face, vip, level) = await ReadAccountProfileAsync(cookieForServer, mid, logger).ConfigureAwait(false);
        logger?.LogInformation(
            "画质解锁 ui 字段：mid={Mid} name={Name} face={HasFace} vip={Vip} level={Level}",
            mid,
            string.IsNullOrEmpty(name) ? "空" : name,
            string.IsNullOrEmpty(face) ? "无" : "有",
            vip,
            level);

        var sessdata = ExtractCookieValue(cookieForServer, "SESSDATA");
        var biliJct = ExtractCookieValue(cookieForServer, "bili_jct");
        var buvid3Value = ExtractCookieValue(cookieForServer, "buvid3");
        var dedeUserId = ExtractCookieValue(cookieForServer, "DedeUserID");

        return $"{{\"ua\":\"{Escape(UserAgent)}\",\"mid\":\"{Escape(mid)}\","
            + $"\"vip\":{(vip ? 1 : 0)},\"level\":{level},\"money\":0,"
            + $"\"cookie\":\"{Escape(cookieForServer)}\",\"csrf\":\"{Escape(csrf)}\","
            + $"\"name\":\"{Escape(name)}\",\"face\":\"{Escape(face)}\","
            + $"\"sessdata\":\"{Escape(sessdata)}\",\"bili_jct\":\"{Escape(biliJct)}\","
            + $"\"buvid3\":\"{Escape(buvid3Value)}\",\"dedeuserid\":\"{Escape(dedeUserId)}\"}}";
    }

    /// <summary>
    /// 查视频的 aid：解析服务的 url.query 里要求 avid，浏览器的 playurl 请求也同时带 avid 与 bvid.
    /// 拿不到就返回 0，调用方按 0 处理.
    /// </summary>
    /// <summary>
    /// 把「可能是 avid 的标识」统一成 BV 号；已经是 BV 号就原样返回.
    /// </summary>
    private static async Task<string> NormalizeBvidAsync(string bvid, string cookie, ILogger? logger)
    {
        if (!string.IsNullOrEmpty(bvid) && bvid.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
        {
            return bvid;
        }

        var digits = bvid ?? string.Empty;
        if (digits.StartsWith("av", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        if (!long.TryParse(digits, out var aidValue) || aidValue <= 0)
        {
            return bvid;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/web-interface/view?aid={aidValue}");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var real = ParseBvid(text);
            if (!string.IsNullOrEmpty(real))
            {
                logger?.LogInformation("画质解锁：avid={Aid} 归一化成 {Bvid}", aidValue, real);
                return real;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "avid 转 bvid 失败");
        }

        return bvid;
    }

    /// <summary>
    /// 把解析服务返回的地址换到能连通的镜像主机上。
    /// 抓包实测：cn-gdfs-cc-* 这类节点在本机连不通（mpv 直接 Timeout was reached），
    /// 而 upos-sz-mirror* 秒开；签名参数对 host 不敏感，换 host 不影响校验.
    /// </summary>
    internal static string PreferMirrorHost(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url ?? string.Empty;
        }

        try
        {
            var uri = new Uri(url);
            if (!uri.Host.EndsWith("bilivideo.com", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            if (uri.Host.StartsWith("upos-sz-mirror", StringComparison.OrdinalIgnoreCase)
                || uri.Host.StartsWith("upos-hz-mirror", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            var builder = new UriBuilder(uri) { Host = "upos-sz-mirrorhw.bilivideo.com" };
            return builder.Uri.ToString();
        }
        catch
        {
            return url;
        }
    }
    private static string ParseBvid(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("bvid", out var node))
            {
                return node.GetString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }
    private static async Task<long> GetAidAsync(string bvid, string cookie, ILogger? logger)
    {
        if (string.IsNullOrEmpty(bvid))
        {
            return 0;
        }

        if (!bvid.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(bvid, out var direct) ? direct : 0;
        }

        try
        {
            using var request = CreateRequest($"https://api.bilibili.com/x/web-interface/view?bvid={Uri.EscapeDataString(bvid)}");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("aid", out var aidNode))
            {
                return aidNode.GetInt64();
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "画质解锁：查询 avid 失败");
        }

        return 0;
    }

    /// <summary>
    /// 把请求发给解析服务器并解析返回（bzview2 与 bzview3 共用）.
    /// </summary>
    private static async Task<QualityUnlockResult> SendToServerAsync(
        string apiPath,
        string ui,
        string body,
        string context,
        ILogger? logger)
    {
        try
        {
            // 注册只在普通视频那条链路上做过，番剧接口不带这一步.
            if (apiPath.Equals("api/bzview2", StringComparison.Ordinal))
            {
                await RegisterAsync(ui, logger).ConfigureAwait(false);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Server}/{apiPath}")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Origin", Server);

            // 脚本是在 hd2a 页面上发的请求，Referer 也照抄，免得服务端校验来源时判非法.
            request.Headers.TryAddWithoutValidation("Referer", $"{Server}/hd2a/");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("画质解锁请求失败：HTTP {Status}", (int)response.StatusCode);
                return QualityUnlockResult.Failed(
                    $"HTTP {(int)response.StatusCode}",
                    $"{context} · 卡在=服务器响应 · 返回={Shorten(text, 120)}");
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
                return QualityUnlockResult.Failed(
                    message,
                    $"{context} · 卡在=服务端替我们取 playurl · code={code} · 服务器返回={Shorten(text, 120)}");
            }

            var (video, audio, quality) = ParseDash(root);
            if (string.IsNullOrEmpty(video) && string.IsNullOrEmpty(audio))
            {
                logger?.LogWarning("画质解锁返回里没有 dash 地址");
                return QualityUnlockResult.Failed(
                    "返回数据里没有可用地址",
                    $"{context} · 卡在=解析返回（code=0 但没有 dash）");
            }

            logger?.LogInformation("画质解锁成功：qn={Quality}", quality);
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
            return QualityUnlockResult.Failed(
                ex.Message,
                $"{context} · 卡在=请求服务器（{ex.GetType().Name}）");
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
        // 番剧那条（ogv/player/playview）不是这个形状，dash 可能埋在 data.result 或
        // data.result.video_info 下面，所以逐层往下找，找到哪层用哪层.
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            root = data;
        }

        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            root = result;
        }

        if (root.TryGetProperty("video_info", out var videoInfo) && videoInfo.ValueKind == JsonValueKind.Object)
        {
            root = videoInfo;
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

    /// <summary>
    /// 有就跳过，没有才追加一个 cookie 字段.
    /// </summary>
    private static string AppendCookieValue(string cookie, string name, string? value)
    {
        if (string.IsNullOrEmpty(value)
            || cookie.Contains($"{name}=", StringComparison.OrdinalIgnoreCase))
        {
            return cookie;
        }

        return string.IsNullOrEmpty(cookie)
            ? $"{name}={value}"
            : $"{cookie.TrimEnd(';')}; {name}={value}";
    }

    /// <summary>
    /// 从整串 cookie 里取某一个字段的值，取不到就返回空串.
    /// </summary>
    private static string ExtractCookieValue(string? cookie, string name)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return string.Empty;
        }

        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 截断服务器返回体，诊断信息里只留一小段.
    /// </summary>
    private static string Shorten(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "空";
        }

        var text = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

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

    /// <summary>
    /// 诊断用的详细信息：走的哪个服务器、签名和风控标记有没有、卡在哪一步.
    /// </summary>
    public string? Diagnostic { get; init; }

    public static QualityUnlockResult Failed(string message, string? diagnostic = null)
        => new() { Success = false, Message = message, Diagnostic = diagnostic };
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












