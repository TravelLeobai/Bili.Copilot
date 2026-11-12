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
        // 网页端 create/dyn + scene=4 + web_repost_src：这条是真转发（挂在原动态下面），不是发链接.
        return await PublishAsync(content, cookie, allowInteract, logger, SceneRepost, dynamicId, "转发动态").ConfigureAwait(false);
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
            // 0/1 才能被服务端接受；写成布尔会直接回「请求错误」.
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

                // 发完回查一次：确认「允许评论」真的写进去了，而不是只改了报文.
                var commentState = string.IsNullOrEmpty(dynId)
                    ? string.Empty
                    : await CheckCommentStateAsync(dynId!, cookie, logger).ConfigureAwait(false);
                var suffix = string.IsNullOrEmpty(dynId) ? string.Empty : $"（{dynId}）";
                return (true, $"{action}成功{suffix}{commentState}");
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

    /// <summary>
    /// 回查刚发出去的动态，确认评论开关的实际状态.
    /// 判据：basic.comment_id_str 非空 = 评论开着；为空 = 评论已关.
    /// 刚发出来的动态要过一会儿才进详情索引（会回 MISS_OPUS_DETAIL_DATA），
    /// 所以先重试两次，还不行就翻自己的空间动态去找这一条.
    /// </summary>
    /// <param name="dynId">动态 id.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="logger">日志.</param>
    /// <returns>给用户看的补充说明.</returns>
    private static async Task<string> CheckCommentStateAsync(string dynId, string cookie, ILogger? logger)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                }

                var state = await ReadCommentStateFromDetailAsync(dynId, cookie, logger).ConfigureAwait(false);
                if (state is not null)
                {
                    return DescribeCommentState(state.Value.HasComment, state.Value.Count, state.Value.Forbidden, dynId, logger);
                }
            }

            // 详情接口还没索引到：翻自己的空间动态，按 id 找这一条.
            var fromFeed = await ReadCommentStateFromSpaceAsync(dynId, cookie, logger).ConfigureAwait(false);
            if (fromFeed is not null)
            {
                return DescribeCommentState(fromFeed.Value.HasComment, fromFeed.Value.Count, fromFeed.Value.Forbidden, dynId, logger);
            }

            logger?.LogWarning("回查动态评论状态：详情与空间列表都没找到 {DynId}", dynId);
            return string.Empty;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "回查动态评论状态出错");
            return string.Empty;
        }
    }

    /// <summary>
    /// 把详情接口里能看到的评论字段如实报出来.
    /// 注意：能不能评论在看客视角上表现为 comment.forbidden，
    /// 关闭评论时详情接口未必换一套字段，所以这里不硬断言"开/关".
    /// </summary>
    private static string DescribeCommentState(bool hasComment, int count, bool forbidden, string dynId, ILogger? logger)
    {
        logger?.LogInformation(
            "动态 {DynId} 评论字段：comment_id_str={HasId} forbidden={Forbidden} 现有评论 {Count} 条",
            dynId,
            hasComment ? "有" : "无",
            forbidden,
            count);

        return forbidden
            ? " · 接口标记：不可评论"
            : $" · 接口标记：可评论（{count} 条）";
    }

    /// <summary>
    /// 诊断用：把某条动态的 basic / module_stat 原样打出来，用来确认「评论是否关闭」到底看哪个字段.
    /// </summary>
    /// <param name="dynId">动态 id.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="logger">日志.</param>
    /// <returns>等待任务.</returns>
    internal static async Task DumpDynamicAsync(string dynId, string cookie, ILogger? logger)
    {
        try
        {
            var text = await GetAsync($"https://api.bilibili.com/x/polymer/web-dynamic/v1/detail?id={Uri.EscapeDataString(dynId)}", cookie).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("item", out var item))
            {
                logger?.LogInformation("诊断 {DynId}: 详情没数据 {Head}", dynId, text.Length > 150 ? text[..150] : text);
                return;
            }

            var basic = item.TryGetProperty("basic", out var b) ? b.GetRawText() : "(无)";
            var stat = item.TryGetProperty("modules", out var m) && m.TryGetProperty("module_stat", out var s) ? s.GetRawText() : "(无)";
            logger?.LogInformation("诊断 {DynId} basic={Basic}", dynId, basic.Length > 600 ? basic[..600] : basic);
            logger?.LogInformation("诊断 {DynId} module_stat={Stat}", dynId, stat.Length > 600 ? stat[..600] : stat);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "诊断 {DynId} 出错", dynId);
        }
    }

    private static async Task<(bool HasComment, int Count, bool Forbidden)?> ReadCommentStateFromDetailAsync(
        string dynId,
        string cookie,
        ILogger? logger)
    {
        var text = await GetAsync(
            $"https://api.bilibili.com/x/polymer/web-dynamic/v1/detail?id={Uri.EscapeDataString(dynId)}",
            cookie).ConfigureAwait(false);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var codeNode) || codeNode.GetInt32() != 0)
        {
            logger?.LogDebug(
                "详情接口暂未返回（{Head}）",
                text.Length > 120 ? text[..120] : text);
            return null;
        }

        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("item", out var item))
        {
            return null;
        }

        return ReadStateFromItem(item);
    }

    private static async Task<(bool HasComment, int Count, bool Forbidden)?> ReadCommentStateFromSpaceAsync(
        string dynId,
        string cookie,
        ILogger? logger)
    {
        var mid = ExtractCookie(cookie, "DedeUserID");
        if (string.IsNullOrWhiteSpace(mid))
        {
            return null;
        }

        var text = await GetAsync(
            $"https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?host_mid={Uri.EscapeDataString(mid)}&features=itemOpusStyle",
            cookie).ConfigureAwait(false);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var codeNode) || codeNode.GetInt32() != 0
            || !root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            logger?.LogDebug("空间动态列表没拿到（{Head}）", text.Length > 120 ? text[..120] : text);
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("id_str", out var idNode)
                && string.Equals(idNode.GetString(), dynId, StringComparison.Ordinal))
            {
                return ReadStateFromItem(item);
            }
        }

        return null;
    }

    private static (bool HasComment, int Count, bool Forbidden) ReadStateFromItem(JsonElement item)
    {
        var hasComment = item.TryGetProperty("basic", out var basic)
            && basic.TryGetProperty("comment_id_str", out var cid)
            && !string.IsNullOrEmpty(cid.GetString());

        var count = 0;
        var forbidden = false;
        if (item.TryGetProperty("modules", out var modules)
            && modules.TryGetProperty("module_stat", out var stat)
            && stat.TryGetProperty("comment", out var comment))
        {
            if (comment.TryGetProperty("count", out var countNode) && countNode.ValueKind == JsonValueKind.Number)
            {
                count = countNode.GetInt32();
            }

            if (comment.TryGetProperty("forbidden", out var forbiddenNode) && forbiddenNode.ValueKind == JsonValueKind.True)
            {
                forbidden = true;
            }
        }

        return (hasComment, count, forbidden);
    }

    private static async Task<string> GetAsync(string url, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", "https://t.bilibili.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://t.bilibili.com");
        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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












