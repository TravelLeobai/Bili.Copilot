// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Bili.Authorization;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 访问令牌（access_key）的取用入口.
/// 登录之后 BiliKernel 自己就持有 token（<see cref="IBiliTokenResolver"/>），
/// 不需要再走 "网页登录态换令牌" 那两跳——那条路已经下线（见 <see cref="TryExchangeFromCookieAsync"/> 注释）.
/// </summary>
internal static class BiliAccessTokenService
{
    /// <summary>
    /// 取当前登录态的 access_token；没登录时返回 <c>null</c>.
    /// </summary>
    /// <param name="tokenResolver">BiliKernel 的令牌来源.</param>
    /// <returns>access_token 或 <c>null</c>.</returns>
    public static string? GetAccessToken(IBiliTokenResolver tokenResolver)
        => tokenResolver?.GetToken()?.AccessToken;

    /// <summary>
    /// 启动时跑一次的默认动作：把令牌状态记进日志（打码），方便排查"有没有令牌".
    /// </summary>
    /// <param name="tokenResolver">BiliKernel 的令牌来源.</param>
    /// <returns>是否拿到令牌.</returns>
    public static bool LogTokenState(IBiliTokenResolver tokenResolver)
    {
        var token = tokenResolver?.GetToken();
        var accessKey = token?.AccessToken;
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            Serilog.Log.Information("访问令牌：当前没有 access_key（未登录或登录态未带令牌）");
            return false;
        }

        // ExpiresIn 可能是「剩余秒数」，也可能是「到期时间戳」，两种都兜住.
        var remaining = token!.ExpiresIn > 1_000_000_000L
            ? DateTimeOffset.FromUnixTimeSeconds(token.ExpiresIn) - DateTimeOffset.Now
            : TimeSpan.FromSeconds(Math.Max(0, token.ExpiresIn));
        Serilog.Log.Information(
            "访问令牌：已就绪 access_token={Masked} uid={Uid} 剩余有效期≈{Hours:F1}h",
            Mask(accessKey),
            token.UserId,
            Math.Max(0, remaining.TotalHours));
        return true;
    }

    /// <summary>
    /// 旧的"网页登录态换 App 令牌"两跳（login/app/third → confirm_uri）.
    /// 2026-11-12 实测：所有已公开 appkey 都返回
    /// <c>code=20000 该版本已不支持当前功能，请升级新版本</c>，接口已下线，
    /// 所以这里只保留说明，不再发请求——真正拿令牌靠 <see cref="GetAccessToken"/>.
    /// </summary>
    /// <returns>恒为 <c>null</c>.</returns>
    public static Task<string?> TryExchangeFromCookieAsync()
    {
        Serilog.Log.Debug("login/app/third 两跳换令牌接口已下线（code=20000），跳过");
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// 令牌打码：只留前四位.
    /// </summary>
    private static string Mask(string token)
        => token.Length <= 4 ? "****" : token[..4] + "****";
}

