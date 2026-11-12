// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Services.Live;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 把内容发到自己的动态时间线 / 转发别人的动态.
/// 走 opus 的 <c>x/dynamic/feed/create/dyn</c>：wbi 签名 + JSON 的 dyn_req，
/// 认证只要 cookie 里的 SESSDATA + bili_jct（csrf）.
/// 转发不是"发一条带链接的文字"：网页端转发走的是同一个接口，但有两点不同——
/// <c>dyn_req.scene = 4</c>，且根节点多一个 <c>web_repost_src.dyn_id_str</c> 指向原动态.
/// </summary>
public static class DynamicShareService
{
    private const string CreateUrl = "https://api.bilibili.com/x/dynamic/feed/create/dyn";
    /// <summary>
    /// vc 老接口是否可用.
    /// 实测（2026-11-12）：dynamic_repost/repost 返回 HTTP 404（HTML），dynamic_svr/create 返回
    /// 4126001 请求数据发生错误——两个接口都已经不可用，留着只会每次转发白跑一次请求，
    /// 所以默认关掉；万一以后恢复，把这个开关打开即可.
    /// </summary>
    private static readonly bool UseLegacyVcApi = false;

    private const string VcRepostUrl = "https://api.vc.bilibili.com/dynamic_repost/v1/dynamic_repost/repost";

    /// <summary>vc 发动态（视频等）.</summary>
    private const string VcCreateUrl = "https://api.vc.bilibili.com/dynamic_svr/v1/dynamic_svr/create";

    /// <summary>普通动态的 scene.</summary>
    private const int SceneText = 1;

    /// <summary>转发的 scene（网页端实测值）.</summary>
    private const int SceneRepost = 4;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = IpV4Connector.ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// 转发一条动态（真正挂在原动态下面，不是发链接）.
    /// </summary>
    /// <param name="dynamicId">被转发动态的 id.</param>
    /// <param name="content">附言.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="allowInteract">是否允许别人评论和转发.</param>
    /// <param name="logger">日志.</param>
    /// <returns>成功与否 + 提示文本.</returns>
    public static async Task<(bool Success, string Message)> RepostAsync(
        string dynamicId,
        string content,
        string cookie,
        bool allowInteract = true,
        ILogger? logger = null)
    {
        string? vcMessage = null;
        if (UseLegacyVcApi)
        {
            var vc = await VcRepostAsync(dynamicId, content, cookie, logger).ConfigureAwait(false);
            if (vc.Success)
            {
                return vc;
            }

            vcMessage = vc.Message;
            logger?.LogWarning("vc 转发未成功（{Message}），改用网页端 create/dyn 兜底", vc.Message);
        }

        // 网页端 create/dyn + scene=4 + web_repost_src：这条是真转发（挂在原动态下面），不是发链接.
        var web = await PublishAsync(content, cookie, allowInteract, logger, SceneRepost, dynamicId, "转发动态").ConfigureAwait(false);
        return web.Success || vcMessage is null
            ? web
            : (false, $"{vcMessage}；兜底也失败：{web.Message}");
    }

    /// <summary>
    /// 发一条纯文字动态（评论转发、以及视频没有 aid 时的兜底）.
    /// </summary>
    /// <param name="content">正文.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="allowInteract">是否允许别人评论和转发.</param>
    /// <param name="logger">日志.</param>
    /// <returns>成功与否 + 提示文本.</returns>
    public static Task<(bool Success, string Message)> PostTextAsync(
        string content,
        string cookie,
        bool allowInteract = true,
        ILogger? logger = null)
        => PublishAsync(content, cookie, allowInteract, logger, SceneText, null, "发表动态");

    /// <summary>
    /// 把视频作为动态发出去：正文 + 视频链接（B 站会把链接解析成视频卡片）.
    /// </summary>
    /// <param name="aidOrBvid">avid 或 BV 号.</param>
    /// <param name="content">附言.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="allowInteract">是否允许别人评论和转发.</param>
    /// <param name="logger">日志.</param>
    /// <returns>成功与否 + 提示文本.</returns>
    public static async Task<(bool Success, string Message)> ShareVideoAsync(
        string aidOrBvid,
        string content,
        string cookie,
        bool allowInteract = true,
        ILogger? logger = null)
    {
        var id = aidOrBvid ?? string.Empty;
        var isBvid = id.StartsWith("BV", StringComparison.OrdinalIgnoreCase);

        // B 站只把 BV 链接解析成视频卡，av 链接会原样当普通文本发出去——
        // 拿到的是 aid 时先换成 BV 再发.
        var bvid = isBvid ? id : await ResolveBvidAsync(id, cookie, logger).ConfigureAwait(false);
        var link = string.IsNullOrEmpty(bvid)
            ? $"https://www.bilibili.com/video/av{id}"
            : $"https://www.bilibili.com/video/{bvid}";
        logger?.LogInformation("转发视频链接：{Link}（原始 id {Id}）", link, id);

        // 数字 aid 才有 vc 视频动态可走；BV 号得先换 aid，这里直接用 opus 的链接写法.
        if (UseLegacyVcApi && !isBvid && long.TryParse(id, out _))
        {
            var vc = await VcShareVideoAsync(id, content ?? string.Empty, cookie, logger).ConfigureAwait(false);
            if (vc.Success)
            {
                return vc;
            }

            logger?.LogWarning("vc 视频转发未成功（{Message}），改用网页端 create/dyn 兜底", vc.Message);
        }

        return await PublishAsync(Compose(content, link), cookie, allowInteract, logger, SceneText, null, "转发视频").ConfigureAwait(false);
    }

    /// <summary>
    /// 拼请求体（抽出来便于离线检查结构，不含发送逻辑）.
    /// </summary>
    /// <param name="csrf">bili_jct.</param>
    /// <param name="content">正文.</param>
    /// <param name="scene">1 = 普通动态，4 = 转发.</param>
    /// <param name="allowInteract">是否允许评论和转发.</param>
    /// <param name="repostSourceId">被转发动态 id；普通动态传 null.</param>
    /// <returns>JSON 字符串.</returns>
    internal static string BuildPayload(
        string csrf,
        string content,
        int scene,
        bool allowInteract,
        string? repostSourceId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("csrf", csrf);

            writer.WritePropertyName("dyn_req");
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartObject();
            writer.WritePropertyName("contents");
            writer.WriteStartArray();

            // 转发且没写附言时，contents 是空数组（网页端行为）.
            if (scene != SceneRepost || !string.IsNullOrWhiteSpace(content))
            {
                writer.WriteStartObject();
                writer.WriteString("raw_text", content ?? string.Empty);
                writer.WriteNumber("type", 1);
                writer.WriteString("biz_id", string.Empty);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteNumber("scene", scene);

            writer.WritePropertyName("option");
            writer.WriteStartObject();
            writer.WriteNumber("close_comment", allowInteract ? 0 : 1);
            writer.WriteNumber("close_repost", allowInteract ? 0 : 1);
            writer.WriteEndObject();

            if (scene == SceneRepost)
            {
                writer.WritePropertyName("attach_card");
                writer.WriteNullValue();
            }

            writer.WritePropertyName("meta");
            writer.WriteStartObject();
            writer.WritePropertyName("app_meta");
            writer.WriteStartObject();
            writer.WriteString("from", "create.dynamic.web");
            writer.WriteString("mobi_app", "web");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            if (!string.IsNullOrEmpty(repostSourceId))
            {
                writer.WritePropertyName("web_repost_src");
                writer.WriteStartObject();
                writer.WriteString("dyn_id_str", repostSourceId);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<(bool Success, string Message)> PublishAsync(
        string content,
        string cookie,
        bool allowInteract,
        ILogger? logger,
        int scene,
        string? repostSourceId,
        string action)
    {
        if (string.IsNullOrWhiteSpace(cookie) || !cookie.Contains("SESSDATA=", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "没有登录态，先登录再转发");
        }

        var csrf = ExtractCookie(cookie, "bili_jct");
        if (string.IsNullOrWhiteSpace(csrf))
        {
            return (false, "登录态里缺 bili_jct，重新登录一次");
        }

        try
        {
            var query = await BiliWbiSigner.SignAsync(new Dictionary<string, string> { ["csrf"] = csrf }, cookie, logger).ConfigureAwait(false);
            var url = $"{CreateUrl}?{query}";
            var payloadText = BuildPayload(csrf, content, scene, allowInteract, repostSourceId);

            // 日志里把 csrf 打码，别把凭据写进日志文件.
            logger?.LogInformation("动态报文（{Action}）：{Payload}", action, payloadText.Replace(csrf, "***"));

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payloadText, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", "https://t.bilibili.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://t.bilibili.com");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // 412 是风控页（HTML），别再重试，直接报出去.
            if ((int)response.StatusCode == 412)
            {
                logger?.LogWarning("动态{Action}被风控（HTTP 412），不重试", action);
                return (false, "被 B 站风控拦下（HTTP 412），稍后再试，别连续重试");
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeNode) ? codeNode.GetInt32() : -1;
            var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : null;

            if (code == 0)
            {
                var dynId = ReadDynamicId(root);
                logger?.LogInformation("动态{Action}成功：{DynId}", action, dynId);
                return (true, string.IsNullOrEmpty(dynId) ? $"{action}成功" : $"{action}成功（{dynId}）");
            }

            logger?.LogWarning(
                "动态{Action}失败：code={Code} message={Message} body={Body}",
                action,
                code,
                message,
                text.Length > 300 ? text[..300] : text);
            return (false, string.IsNullOrWhiteSpace(message) ? $"{action}失败（code={code}）" : message!);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "动态{Action}出错", action);
            return (false, $"{action}出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 用 vc 的 dynamic_svr/create 发一条视频动态（type=8，rid=aid）.
    /// 有 TV 授权时带 access_key 并用 TV 的 appkey 签名；没有就走 Android 签名.
    /// </summary>
    /// <param name="aid">视频 avid.</param>
    /// <param name="content">附言.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="logger">日志.</param>
    /// <returns>成功与否 + 提示文本.</returns>
    private static async Task<(bool Success, string Message)> VcShareVideoAsync(
        string aid,
        string content,
        string cookie,
        ILogger? logger)
    {
        var uid = ExtractCookie(cookie, "DedeUserID");
        if (string.IsNullOrWhiteSpace(uid) && BiliOAuthService.Mid > 0)
        {
            uid = BiliOAuthService.Mid.ToString();
        }

        if (string.IsNullOrWhiteSpace(uid))
        {
            logger?.LogWarning("vc 视频转发跳过：拿不到 uid");
            return (false, "拿不到账号 uid");
        }

        try
        {
            var accessToken = await BiliOAuthService.GetAccessTokenAsync(default).ConfigureAwait(false);
            var hasTvToken = !string.IsNullOrWhiteSpace(accessToken);
            var query = hasTvToken
                ? BiliOAuthService.BuildSignedTvQuery(new[] { new KeyValuePair<string, string>("access_key", accessToken!) })
                : BiliVcSigner.BuildSignedQuery([]);
            logger?.LogInformation("vc 视频转发签名方式：{Mode}", hasTvToken ? "TV 授权（含 access_key）" : "Android 默认");

            // 字段照 BiliUWP-Lite 的 CreateDynamicText/CreateDynamicPhoto 抄齐：
            // 少了 setting / jumpfrom / up_*_comment 会被服务端判成"请求数据发生错误"(4126001).
            var form = new Dictionary<string, string>
            {
                ["uid"] = uid,
                ["dynamic_id"] = "0",
                ["type"] = "8",
                ["rid"] = aid,
                ["content"] = content ?? string.Empty,
                ["setting"] = "{\"copy_forbidden\":0}",
                ["at_uids"] = string.Empty,
                ["at_control"] = string.Empty,
                ["jumpfrom"] = "110",
                ["up_choose_comment"] = "0",
                ["up_close_comment"] = "0",
                ["extension"] = "{\"emoji_type\":1}",
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{VcCreateUrl}?{query}")
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 412)
            {
                logger?.LogWarning("vc 视频转发被风控（HTTP 412）");
                return (false, "vc 接口被风控拦下（HTTP 412）");
            }

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeNode) ? codeNode.GetInt32() : -1;
            var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : null;

            if (code == 0)
            {
                var dynId = ReadDynamicId(root);
                logger?.LogInformation("vc 视频转发成功：{DynId}", dynId);
                return (true, string.IsNullOrEmpty(dynId) ? "转发成功" : $"转发成功（{dynId}）");
            }

            logger?.LogWarning("vc 视频转发失败：code={Code} message={Message}", code, message);
            return (false, string.IsNullOrWhiteSpace(message) ? $"vc 视频转发失败（code={code}）" : $"vc 视频转发失败：{message}");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "vc 视频转发出错");
            return (false, $"vc 视频转发出错：{ex.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> VcRepostAsync(
        string dynamicId,
        string content,
        string cookie,
        ILogger? logger)
    {
        var uid = ExtractCookie(cookie, "DedeUserID");
        if (string.IsNullOrWhiteSpace(uid) && BiliOAuthService.Mid > 0)
        {
            // Cookie 里没有 DedeUserID 时用 TV 授权拿到的 mid 兜底（vc 的 body 必须要 uid）.
            uid = BiliOAuthService.Mid.ToString();
            logger?.LogDebug("vc 转发 uid 取自身份授权：{Uid}", uid);
        }

        if (string.IsNullOrWhiteSpace(uid))
        {
            logger?.LogWarning("vc 转发跳过：拿不到 uid（Cookie 无 DedeUserID，也没做过 TV 授权）");
            return (false, "拿不到账号 uid（Cookie 里没有 DedeUserID，也没做过 TV 授权）");
        }

        try
        {
            // vc 转发要 App 鉴权：设置里做过 TV 扫码就带上 access_key 并用 TV 的 appkey 签名，
            // 没做过就还是原来的 Android 签名（不带 access_key），行为不变.
            var accessToken = await BiliOAuthService.GetAccessTokenAsync(default).ConfigureAwait(false);
            var hasTvToken = !string.IsNullOrWhiteSpace(accessToken);
            var query = hasTvToken
                ? BiliOAuthService.BuildSignedTvQuery(new[] { new KeyValuePair<string, string>("access_key", accessToken!) })
                : BiliVcSigner.BuildSignedQuery([]);
            logger?.LogInformation("vc 转发签名方式：{Mode}", hasTvToken ? "TV 授权（含 access_key）" : "Android 默认");
            var url = $"{VcRepostUrl}?{query}";
            var form = new Dictionary<string, string>
            {
                ["uid"] = uid,
                ["dynamic_id"] = dynamicId,
                ["content"] = content ?? string.Empty,
                ["at_uids"] = string.Empty,
                ["at_control"] = string.Empty,
                ["extension"] = "{\"emoji_type\":1}",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 412)
            {
                logger?.LogWarning("vc 转发被风控（HTTP 412）");
                return (false, "vc 接口被风控拦下（HTTP 412）");
            }

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!text.TrimStart().StartsWith("{"))
            {
                logger?.LogWarning("vc 转发返回非 JSON（HTTP {Status}）：{Head}", status, text.Length > 160 ? text[..160] : text);
                return (false, $"vc 接口返回 HTTP {status} 的非 JSON 内容（多半被风控页拦了）");
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeNode) ? codeNode.GetInt32() : -1;
            var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : null;

            if (code == 0)
            {
                var dynId = ReadDynamicId(root);
                logger?.LogInformation("vc 转发成功：{DynId}", dynId);
                return (true, string.IsNullOrEmpty(dynId) ? "转发成功" : $"转发成功（{dynId}）");
            }

            logger?.LogWarning("vc 转发失败：code={Code} message={Message}", code, message);
            return (false, string.IsNullOrWhiteSpace(message) ? $"vc 转发失败（code={code}）" : $"vc 转发失败：{message}");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "vc 转发出错");
            return (false, $"vc 转发出错：{ex.Message}");
        }
    }


    /// <summary>
    /// aid 转 BV 号；拿不到就返回空（调用方退回 av 链接）.
    /// </summary>
    /// <param name="aid">视频 aid.</param>
    /// <param name="cookie">登录态（可选）.</param>
    /// <param name="logger">日志.</param>
    /// <returns>BV 号或空串.</returns>
    private static async Task<string> ResolveBvidAsync(string aid, string cookie, ILogger? logger)
    {
        if (!long.TryParse(aid, out _))
        {
            return string.Empty;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.bilibili.com/x/web-interface/view?aid={aid}");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("bvid", out var bvidNode)
                && bvidNode.ValueKind == JsonValueKind.String
                ? bvidNode.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "aid 转 BV 失败：{Aid}", aid);
            return string.Empty;
        }
    }

    private static string ReadDynamicId(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (data.TryGetProperty("dyn_id_str", out var strNode))
        {
            return strNode.ValueKind == JsonValueKind.String ? strNode.GetString() ?? string.Empty : strNode.ToString();
        }

        return data.TryGetProperty("dyn_id", out var numNode) ? numNode.ToString() : string.Empty;
    }

    private static string Compose(string? content, string link)
    {
        var text = content?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(text) ? link : $"{text}\n{link}";
    }

    private static string ExtractCookie(string cookie, string name)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return string.Empty;
        }

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









