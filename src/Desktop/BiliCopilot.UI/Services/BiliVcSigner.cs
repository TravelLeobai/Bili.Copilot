// Copyright (c) Bili Copilot. All rights reserved.

using System.Security.Cryptography;
using System.Text;

namespace BiliCopilot.UI.Services;

/// <summary>
/// vc 系接口（api.vc.bilibili.com）的 AppKey 签名.
/// 移植自 BiliUWP-Lite：<c>Services/ApiHelper.cs</c> 的 MustParameter + GetSign
/// （参数按 key 排序拼 <c>k=v&amp;</c>，去掉末尾 &amp;，接 AppSecret，取 MD5 小写）.
/// 参数值参与签名时用原始文本，传输时才做 URL 编码——这点和原实现一致，签错了会 412.
/// </summary>
internal static class BiliVcSigner
{
    /// <summary>Android 端 AppKey（Constants.ANDROID_APP_KEY）.</summary>
    private const string AndroidAppKey = "1d8b6e7d45233436";

    /// <summary>Android 端 AppSecret（ApiHelper.AndroidKey）.</summary>
    private const string AndroidAppSecret = "560c52ccd288fed045859ed18bffd973";

    /// <summary>mobi_app 取值.</summary>
    private const string MobiApp = "android";

    /// <summary>platform 取值.</summary>
    private const string Platform = "android";

    /// <summary>Build 值，对应 BiliUWP-Lite 设置项「发起请求时使用的 Build 值」，默认 75900200.</summary>
    private const string Build = "75900200";

    /// <summary>
    /// 生成带 appkey 与 sign 的 query 串（已 URL 编码，可直接拼在 URL 后面）.
    /// 用 Android 端的 AppKey，对应没有 TV 授权时的默认路径.
    /// </summary>
    /// <param name="parameters">业务参数（不含 appkey/build/mobi_app/platform/ts）.</param>
    /// <returns>形如 <c>a=1&amp;appkey=..&amp;sign=..</c> 的 query.</returns>
    public static string BuildSignedQuery(IEnumerable<KeyValuePair<string, string>> parameters)
        => BuildSignedQuery(parameters, AndroidAppKey, AndroidAppSecret, MobiApp, Platform, Build);

    /// <summary>
    /// 生成带 appkey 与 sign 的 query 串，AppKey / AppSecret 由调用方给.
    /// 用于 TV 授权那条路：签名要用 TV 的 AppKey，并且参数里会多带一个 <c>access_key</c>.
    /// </summary>
    /// <param name="parameters">业务参数（不含 appkey/build/mobi_app/platform/ts）.</param>
    /// <param name="appKey">AppKey.</param>
    /// <param name="appSecret">AppSecret.</param>
    /// <param name="mobiApp">mobi_app 取值.</param>
    /// <param name="platform">platform 取值.</param>
    /// <param name="build">build 取值.</param>
    /// <returns>形如 <c>a=1&amp;appkey=..&amp;sign=..</c> 的 query.</returns>
    public static string BuildSignedQuery(
        IEnumerable<KeyValuePair<string, string>> parameters,
        string appKey,
        string appSecret,
        string mobiApp = "android",
        string platform = "android",
        string build = "75900200")
    {
        // 签名要按 key 排序，值用原文.
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            ordered[pair.Key] = pair.Value ?? string.Empty;
        }

        ordered["appkey"] = appKey;
        ordered["build"] = build;
        ordered["mobi_app"] = mobiApp;
        ordered["platform"] = platform;
        ordered["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var raw = string.Join("&", ordered.Select(pair => $"{pair.Key}={pair.Value}"));
        var sign = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(raw + appSecret)));

        // 传输时才对值做编码.
        var encoded = string.Join("&", ordered.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        return $"{encoded}&sign={sign}";
    }
}
