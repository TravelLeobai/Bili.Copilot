// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.Toolkits;

namespace BiliCopilot.UI.Services;

/// <summary>
/// TV 扫码授权：补一个 <c>access_key</c>，只给 vc 系接口（动态转发）用.
/// 这是可选的补充授权，原有的 cookie 登录体系完全不碰，没授权时一切照旧.
/// 走的三个接口（B 站 TV 端登录）：
/// <list type="bullet">
/// <item><c>qrcode/auth_code</c>：拿二维码 url 与 auth_code</item>
/// <item><c>qrcode/poll</c>：轮询扫码结果（86101 未扫码 / 86039 尚未确认 / 86090 已扫码待确认 / 86038 已过期）</item>
/// <item><c>oauth2/refresh_token</c>：access_token 过期后刷新</item>
/// </list>
/// TV 的 appkey / appsecret 只放在这个类里，日志里也绝不出现 token 原文.
/// </summary>
public static class BiliOAuthService
{
    /// <summary>
    /// TV 授权账号的 mid；没授权过是 0.
    /// </summary>
    public static long Mid => SettingsToolkit.ReadLocalSetting(SettingNames.TvOAuthMid, 0L);

    /// <summary>TV 端 AppKey（passport-tv-login 专用）.</summary>
    private const string TvAppKey = "4409e2ce8ffd12b8";

    /// <summary>TV 端 AppSecret.</summary>
    private const string TvAppSecret = "59b43e04ad6965f34319062b478f83dd";

    /// <summary>TV 登录要求的 local_id，固定 0.</summary>
    private const string LocalId = "0";

    /// <summary>未扫码.</summary>
    private const int CodeNotScanned = 86101;

    /// <summary>二维码尚未确认（实测会先返回这个码，和 86101 一样属于"还在等"）.</summary>
    private const int CodeNotConfirmed = 86039;

    /// <summary>
    /// 轮询结果：已扫码，等待手机端确认.
    /// </summary>
    public const int PollCodeScanned = 86090;

    /// <summary>
    /// 轮询结果：二维码已过期.
    /// </summary>
    public const int PollCodeExpired = 86038;

    private const string AuthCodeUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/auth_code";
    private const string PollUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
    private const string RefreshUrl = "https://passport.bilibili.com/x/passport-tv-login/oauth2/refresh_token";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = IpV4Connector.ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// 本地是否有一枚还没过期的 access_token（设置页用它显示「已授权」）.
    /// </summary>
    public static bool HasToken
        => !string.IsNullOrWhiteSpace(SettingsToolkit.ReadLocalSetting(SettingNames.TvOAuthAccessToken, string.Empty))
            && DateTimeOffset.Now < ExpiresAt;

    /// <summary>
    /// 本地 token 的到期时间；没存过时是 <see cref="DateTimeOffset.UnixEpoch"/>.
    /// </summary>
    public static DateTimeOffset ExpiresAt
        => DateTimeOffset.FromUnixTimeSeconds(SettingsToolkit.ReadLocalSetting(SettingNames.TvOAuthExpiresAt, 0L)).ToLocalTime();

    /// <summary>
    /// 最近一次扫码轮询的结果码：0 = 成功，86101 未扫码，86090 已扫码待确认，86038 已过期.
    /// 弹框靠它区分「还在等」和「二维码过期了」.
    /// </summary>
    public static int LastPollCode { get; private set; }

    /// <summary>
    /// 要一个二维码：返回 auth_code（轮询用）和二维码里的 url（画图用）.
    /// </summary>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>auth_code + 二维码 url.</returns>
    /// <exception cref="InvalidOperationException">接口报错或数据结构不对时抛出.</exception>
    public static async Task<(string AuthCode, string Url)> CreateQrAsync(CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["local_id"] = LocalId,
        };

        var query = BuildPassportQuery(parameters);
        using var document = await PostAsync(AuthCodeUrl, query, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var code = ReadInt(root, "code");
        if (code != 0)
        {
            Serilog.Log.Warning("获取 TV 二维码失败：code={Code} message={Message}", code, ReadString(root, "message"));
            throw new InvalidOperationException($"获取二维码失败（code={code}）：{ReadString(root, "message")}");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            Serilog.Log.Warning("获取 TV 二维码失败：响应里没有 data");
            throw new InvalidOperationException("获取二维码失败：响应里没有 data");
        }

        var authCode = ReadString(data, "auth_code");
        var url = ReadString(data, "url");
        if (string.IsNullOrWhiteSpace(authCode) || string.IsNullOrWhiteSpace(url))
        {
            Serilog.Log.Warning("获取 TV 二维码失败：auth_code 或 url 为空");
            throw new InvalidOperationException("获取二维码失败：返回内容不完整");
        }

        LastPollCode = CodeNotScanned;
        Serilog.Log.Information("TV 二维码已获取，等待扫码");
        return (authCode, url);
    }

    /// <summary>
    /// 轮询一次扫码结果。还没扫完时返回 <c>null</c>（看 <see cref="LastPollCode"/> 分辨是未扫码、待确认还是已过期）.
    /// 扫码成功时把 token 与到期时间写进本地设置，并返回 access_token.
    /// </summary>
    /// <param name="authCode">二维码的 auth_code.</param>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>access_token；未完成时 <c>null</c>.</returns>
    public static async Task<string?> PollAsync(string authCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authCode))
        {
            return null;
        }

        var parameters = new Dictionary<string, string>
        {
            ["auth_code"] = authCode,
            ["local_id"] = LocalId,
        };

        var query = BuildPassportQuery(parameters);
        using var document = await PostAsync(PollUrl, query, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var code = ReadInt(root, "code");
        LastPollCode = code;

        switch (code)
        {
            case 0:
                break;
            case CodeNotScanned:
                Serilog.Log.Debug("TV 扫码：还没扫");
                return null;
            case CodeNotConfirmed:
                // 实测先返回 86039（二维码尚未确认），这不是错误，别打警告刷屏.
                Serilog.Log.Debug("TV 扫码：二维码尚未确认");
                return null;
            case PollCodeScanned:
                Serilog.Log.Debug("TV 扫码：已扫码，等手机确认");
                return null;
            case PollCodeExpired:
                Serilog.Log.Information("TV 扫码：二维码已过期");
                return null;
            default:
                Serilog.Log.Warning("TV 扫码轮询失败：code={Code} message={Message}", code, ReadString(root, "message"));
                return null;
        }

        return PersistToken(root);
    }

    /// <summary>
    /// 拿一个能用的 access_token：本地没过期就直接给；过期了用 refresh_token 刷一次；刷新失败就清掉本地 token 返回 <c>null</c>.
    /// </summary>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>access_token；没有授权时 <c>null</c>.</returns>
    public static async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var token = SettingsToolkit.ReadLocalSetting(SettingNames.TvOAuthAccessToken, string.Empty);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (DateTimeOffset.Now < ExpiresAt)
        {
            return token;
        }

        var refreshToken = SettingsToolkit.ReadLocalSetting(SettingNames.TvOAuthRefreshToken, string.Empty);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            Serilog.Log.Information("TV 授权已过期且没有 refresh_token，清掉本地令牌");
            Clear();
            return null;
        }

        var refreshed = await RefreshAsync(token, refreshToken, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshed))
        {
            Serilog.Log.Warning("刷新 TV 授权失败，清掉本地令牌");
            Clear();
            return null;
        }

        return refreshed;
    }

    /// <summary>
    /// 清掉本地保存的 TV 授权.
    /// </summary>
    public static void Clear()
    {
        SettingsToolkit.DeleteLocalSetting(SettingNames.TvOAuthAccessToken);
        SettingsToolkit.DeleteLocalSetting(SettingNames.TvOAuthRefreshToken);
        SettingsToolkit.DeleteLocalSetting(SettingNames.TvOAuthExpiresAt);
        LastPollCode = 0;
    }

    /// <summary>
    /// 用 TV 的 AppKey 给 vc 接口的参数签名（带 <c>access_key</c> 时走这条）.
    /// </summary>
    /// <param name="parameters">业务参数.</param>
    /// <returns>签名后的 query.</returns>
    internal static string BuildSignedTvQuery(IEnumerable<KeyValuePair<string, string>> parameters)
        => BiliVcSigner.BuildSignedQuery(parameters, TvAppKey, TvAppSecret);

    /// <summary>
    /// 只打码后的前缀，别把 token 原文写进日志.
    /// </summary>
    private static string Mask(string token)
        => string.IsNullOrEmpty(token)
            ? "(empty)"
            : (token.Length <= 4 ? token[..1] : token[..4]) + "****";

    /// <summary>
    /// passport 系接口的签名：只带 appkey / ts 和业务参数，不像 vc 那样有 build / mobi_app / platform.
    /// </summary>
    private static string BuildPassportQuery(IReadOnlyDictionary<string, string> parameters)
    {
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            ordered[pair.Key] = pair.Value ?? string.Empty;
        }

        ordered["appkey"] = TvAppKey;
        ordered["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var raw = string.Join("&", ordered.Select(pair => $"{pair.Key}={pair.Value}"));
        var sign = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(raw + TvAppSecret)));
        var encoded = string.Join("&", ordered.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        return $"{encoded}&sign={sign}";
    }

    private static async Task<JsonDocument> PostAsync(string url, string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}?{query}")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://passport.bilibili.com/");

        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Serilog.Log.Warning("TV 授权接口 HTTP {Status}", (int)response.StatusCode);
            throw new HttpRequestException($"授权接口返回 HTTP {(int)response.StatusCode}");
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // 风控页是 HTML，这里不会是 JSON，别把整页打出来.
            Serilog.Log.Warning("TV 授权接口返回的不是 JSON（可能被风控）");
            throw new InvalidOperationException("授权接口返回的不是 JSON，可能被风控拦下了");
        }
    }

    private static async Task<string?> RefreshAsync(string accessToken, string refreshToken, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["access_key"] = accessToken,
            ["refresh_token"] = refreshToken,
        };

        var query = BuildPassportQuery(parameters);
        using var document = await PostAsync(RefreshUrl, query, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var code = ReadInt(root, "code");
        if (code != 0)
        {
            Serilog.Log.Warning("刷新 TV 授权失败：code={Code} message={Message}", code, ReadString(root, "message"));
            return null;
        }

        return PersistToken(root);
    }

    /// <summary>
    /// 把 data 里的 token 落盘并返回 access_token.
    /// </summary>
    private static string? PersistToken(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            Serilog.Log.Warning("TV 授权成功但响应里没有 data");
            return null;
        }

        var accessToken = ReadString(data, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Serilog.Log.Warning("TV 授权成功但 access_token 为空");
            return null;
        }

        var refreshToken = ReadString(data, "refresh_token");
        var expiresIn = data.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt64(out var seconds)
            ? seconds
            : 0L;
        var validSeconds = Math.Max(expiresIn - 60, 60);

        SettingsToolkit.WriteLocalSetting(SettingNames.TvOAuthAccessToken, accessToken);
        SettingsToolkit.WriteLocalSetting(SettingNames.TvOAuthRefreshToken, refreshToken ?? string.Empty);
        SettingsToolkit.WriteLocalSetting(SettingNames.TvOAuthExpiresAt, DateTimeOffset.Now.AddSeconds(validSeconds).ToUnixTimeSeconds());

        // vc 系列接口要求 body 里带 uid：Cookie 里没有 DedeUserID 时用它兜底.
        if (data.TryGetProperty("mid", out var midNode))
        {
            var mid = midNode.ValueKind == System.Text.Json.JsonValueKind.String
                ? (long.TryParse(midNode.GetString(), out var parsed) ? parsed : 0L)
                : (midNode.TryGetInt64(out var num) ? num : 0L);
            if (mid > 0)
            {
                SettingsToolkit.WriteLocalSetting(SettingNames.TvOAuthMid, mid);
            }
        }

        LastPollCode = 0;
        Serilog.Log.Information("TV 授权成功：access_token={Masked}", Mask(accessToken));
        return accessToken;
    }

    private static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var node) && node.TryGetInt32(out var value) ? value : -1;

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
}
