// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Services.Live;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 把内容发到自己的动态时间线.
/// 走 opus 的新接口 x/dynamic/feed/create/dyn：wbi 签名 + JSON 的 dyn_req，
/// 认证只要 cookie 里的 SESSDATA + bili_jct（csrf）.
/// 转发统一用「正文 + 原内容链接」的写法：B 站会把链接解析成对应卡片，
/// 比去猜各类型动态的 dyn_req 结构可靠.
/// </summary>
public static class DynamicShareService
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = IpV4Connector.ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// 转发一条动态：正文 + 原动态链接.
    /// </summary>
    /// <param name="dynamicId">被转发动态的 id.</param>
    /// <param name="content">附言.</param>
    /// <param name="cookie">登录态.</param>
    /// <param name="logger">日志.</param>
    /// <returns>成功与否 + 提示文本.</returns>
    public static Task<(bool Success, string Message)> RepostAsync(
        string dynamicId,
        string content,
        string cookie,
        ILogger? logger = null)
        => PostTextAsync(Compose(content, $"https://t.bilibili.com/{dynamicId}"), cookie, logger);

    /// <summary>
    /// 发一条纯文字动态（评论转发也走这里）.
    /// </summary>
    public static async Task<(bool Success, string Message)> PostTextAsync(
        string content,
        string cookie,
        ILogger? logger = null)
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
            var url = $"https://api.bilibili.com/x/dynamic/feed/create/dyn?{query}";

            using var payload = new MemoryStream();
            using (var writer = new Utf8JsonWriter(payload))
            {
                writer.WriteStartObject();
                writer.WriteString("csrf", csrf);
                writer.WritePropertyName("dyn_req");
                writer.WriteStartObject();
                writer.WritePropertyName("content");
                writer.WriteStartObject();
                writer.WritePropertyName("contents");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("raw_text", content ?? string.Empty);
                writer.WriteNumber("type", 1);
                writer.WriteString("biz_id", string.Empty);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteNumber("scene", 1);
                writer.WritePropertyName("meta");
                writer.WriteStartObject();
                writer.WritePropertyName("app_meta");
                writer.WriteStartObject();
                writer.WriteString("from", "create.dynamic.web");
                writer.WriteString("mobi_app", "web");
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(Encoding.UTF8.GetString(payload.ToArray()), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", "https://t.bilibili.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://t.bilibili.com");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeNode) ? codeNode.GetInt32() : -1;
            var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : null;

            if (code == 0)
            {
                var dynId = string.Empty;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("dyn_id_str", out var strNode))
                    {
                        dynId = strNode.ValueKind == JsonValueKind.String ? strNode.GetString() ?? string.Empty : strNode.ToString();
                    }
                    else if (data.TryGetProperty("dyn_id", out var numNode))
                    {
                        dynId = numNode.ToString();
                    }
                }

                logger?.LogInformation("动态发表成功：{DynId}", dynId);
                return (true, string.IsNullOrEmpty(dynId) ? "发表成功" : $"发表成功（{dynId}）");
            }

            logger?.LogWarning("动态发表失败：code={Code} message={Message} body={Body}", code, message, text.Length > 300 ? text[..300] : text);
            return (false, string.IsNullOrWhiteSpace(message) ? $"发表失败（code={code}）" : message!);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "动态发表出错");
            return (false, $"发表出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 把视频作为动态发出去：正文 + 视频链接（B 站会把链接解析成视频卡片）.
    /// </summary>
    public static Task<(bool Success, string Message)> ShareVideoAsync(
        string aidOrBvid,
        string content,
        string cookie,
        ILogger? logger = null)
    {
        var id = aidOrBvid ?? string.Empty;
        var link = id.StartsWith("BV", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.bilibili.com/video/{id}"
            : $"https://www.bilibili.com/video/av{id}";
        return PostTextAsync(Compose(content, link), cookie, logger);
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

