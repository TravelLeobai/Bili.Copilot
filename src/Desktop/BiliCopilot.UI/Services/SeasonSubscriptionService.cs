// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Bili.Authorization;
using System.Net;
using System.Text.Json;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 订阅合集服务.
/// 内核没有「订阅/取消订阅合集」的接口，这里直接照 biliuwp-lite 的做法打收藏接口：
/// 取消用 x/v3/fav/season/unfav，订阅用 x/v3/fav/season/fav，都要 csrf（bili_jct）.
/// </summary>
public sealed class SeasonSubscriptionService
{
    private const string LiveReferer = "https://www.bilibili.com/";
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly IBiliCookiesResolver _cookies;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonSubscriptionService"/> class.
    /// </summary>
    /// <param name="cookies">Cookie 解析器.</param>
    public SeasonSubscriptionService(IBiliCookiesResolver cookies) => _cookies = cookies;

    /// <summary>
    /// 订阅或取消订阅一个合集.
    /// </summary>
    /// <param name="seasonId">合集 id.</param>
    /// <param name="subscribe">true 订阅，false 取消.</param>
    /// <returns>是否成功.</returns>
    public async Task<bool> SetSubscribedAsync(string seasonId, bool subscribe)
    {
        if (string.IsNullOrEmpty(seasonId))
        {
            return false;
        }

        var cookie = _cookies.GetCookieString();
        var csrf = ExtractCookie(cookie, "bili_jct");
        if (string.IsNullOrEmpty(csrf))
        {
            return false;
        }

        var url = subscribe
            ? "https://api.bilibili.com/x/v3/fav/season/fav"
            : "https://api.bilibili.com/x/v3/fav/season/unfav";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["season_id"] = seasonId,
                    ["csrf"] = csrf,
                    ["platform"] = "web",
                }),
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("Referer", LiveReferer);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractCookie(string cookie, string key)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0 && part[..index].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return part[(index + 1)..];
            }
        }

        return string.Empty;
    }
}

