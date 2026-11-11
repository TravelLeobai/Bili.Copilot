// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Services.Live;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 转发动态 / 视频 / 评论到自己的动态时间线.
/// 走的是 Web 的 x/dynamic/feed/create/dyn（wbi 签名 + bili_jct 防伪），
/// 参考 biliuwp-lite 里 RepostDynamic 的做法（老版 vc 接口已不走签名了，这里用新版）.
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
    /// 转发一条动态（dyn_type=1）.
    /// </summary>
    /// <param name="dynamicId">被转发动态的 id（dyn_id_str）.</param>
    /// <param name="content">附言.</param>
    /// <param name="cookie">登录态（需要 SESSDATA 与 bili_jct）.</param>
    /// <param name="logger">日志.</param>
    /// <returns>成功与否 + 提示文本.</returns>
    public static Task<(bool Success, string Message)> RepostAsync(
        string dynamicId,
        string content,
        string cookie,
        ILogger? logger = null)
        => PostAsync(
            new Dictionary<string, string>
            {
                ["dyn_type"] = "1",
                ["dyn_id_str"] = dynamicId,
                ["content"] = content ?? string.Empty,
                ["at_uids"] = string.Empty,
                ["at_control"] = string.Empty,
            },
            cookie,
            "转发动态",
            logger);

    /// <summary>
    /// 发一条纯文字动态（dyn_type=4）。评论按等级不高、拿不到动态 id 时用它兜底.
    /// </summary>
    public static Task<(bool Success, string Message)> PostTextAsync(
        string content,
        string cookie,
        ILogger? logger = null)
        => PostAsync(
            new Dictionary<string, string>
            {
                ["dyn_type"] = "4",
                ["content"] = content ?? string.Empty,
                ["at_uids"] = string.Empty,
                ["at_control"] = string.Empty,
            },
            cookie,
            "发表动态",
            logger);

    /// <summary>
    /// 把视频作为视频动态转发（dyn_type=8，rid=aid）.
    /// </summary>
    public static Task<(bool Success, string Message)> ShareVideoAsync(
        string aid,
        string content,
        string cookie,
        ILogger? logger = null)
        => PostAsync(
            new Dictionary<string, string>
            {
                ["dyn_type"] = "8",
                ["rid"] = aid,
                ["content"] = content ?? string.Empty,
                ["at_uids"] = string.Empty,
                ["at_control"] = string.Empty,
            },
            cookie,
            "转发视频",
            logger);

    private static async Task<(bool Success, string Message)> PostAsync(
        Dictionary<string, string> payload,
        string cookie,
        string action,
        ILogger? logger)
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
            payload["csrf"] = csrf;
            var signParams = new Dictionary<string, string>
            {
                ["dyn_type"] = payload["dyn_type"],
            };
            if (payload.TryGetValue("dyn_id_str", out var dynId))
            {
                signParams["dyn_id_str"] = dynId;
            }

            if (payload.TryGetValue("rid", out var rid))
            {
                signParams["rid"] = rid;
            }

            var query = await BiliWbiSigner.SignAsync(signParams, cookie, logger).ConfigureAwait(false);
            var url = $"https://api.bilibili.com/x/dynamic/feed/create/dyn?{query}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(payload),
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
                logger?.LogInformation("动态{Action}成功", action);
                return (true, $"{action}成功");
            }

            logger?.LogWarning("动态{Action}失败：code={Code} message={Message}", action, code, message);
            return (false, string.IsNullOrWhiteSpace(message) ? $"{action}失败（code={code}）" : message!);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "动态{Action}出错", action);
            return (false, $"{action}出错：{ex.Message}");
        }
    }

    private static string ExtractCookie(string cookie, string name)
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
