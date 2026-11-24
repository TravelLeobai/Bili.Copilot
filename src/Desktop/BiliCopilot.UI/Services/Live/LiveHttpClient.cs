// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 直播接口里原来「每个方法自己 new 一个 HttpClient」的那几处，收成这一个共享实例.
/// <para>
/// 关注直播列表 / 开播提醒轮询 / 缓存卡片核对 三个入口各自会发 1~12 个请求，
/// 原来每次调用都新建 handler：连接池随 using 一起丢掉，每个请求都要重新握手。
/// 共享一个实例后，同一进程内复用连接.
/// </para>
/// <para>
/// <c>UseCookies = false</c> 是刻意保留的老行为：原来每次都是新 handler，
/// 服务端下发的 cookie 不会跨调用累积；共享 handler 必须显式关掉 cookie 容器，
/// 才不会把这个语义悄悄改掉.
/// </para>
/// </summary>
internal static class LiveHttpClient
{
    /// <summary>
    /// 批量查开播状态的接口地址.
    /// </summary>
    public const string StatusApi = "https://api.live.bilibili.com/room/v1/Room/get_status_info_by_uids";

    /// <summary>
    /// 共用的 HTTP 客户端（连接池跨调用复用）.
    /// </summary>
    public static readonly HttpClient Instance = Create();

    /// <summary>
    /// 拼 <c>{"uids":[1,2,3]}</c> 请求体.
    /// 原来走 StringBuilder + string.Join 两趟，这里一趟插值就够.
    /// </summary>
    /// <param name="uids">用户 id.</param>
    /// <returns>JSON 请求体.</returns>
    public static string BuildUidsBody(IEnumerable<long> uids)
        => $"{{\"uids\":[{string.Join(',', uids)}]}}";

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),

            // 见类型注释：显式关掉，保持「每次调用都不带服务端 cookie」的原有语义.
            UseCookies = false,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}
