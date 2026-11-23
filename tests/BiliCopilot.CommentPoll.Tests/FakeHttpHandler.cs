// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text;

namespace BiliCopilot.CommentPoll.Tests;

/// <summary>
/// 记下每一次请求、按顺序回放预先排好的响应。
/// <para>
/// 所有测试都走它：不碰网络，也绝不对真实账号发写请求（<c>do_vote</c> 只出现在这里）.
/// </para>
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new();

    /// <summary>
    /// Gets 收到的请求（正文已经读成字符串，后续断言可以直接看）.
    /// </summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>
    /// 排一个 JSON 响应.
    /// </summary>
    /// <param name="statusCode">状态码.</param>
    /// <param name="body">正文.</param>
    /// <returns>自己，方便连写.</returns>
    public FakeHttpHandler Enqueue(HttpStatusCode statusCode, string body)
        => Enqueue(_ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    /// <summary>
    /// 排一个响应（要自己看请求内容、或者要卡住不放时用）.
    /// </summary>
    /// <param name="factory">响应工厂.</param>
    /// <returns>自己.</returns>
    public FakeHttpHandler Enqueue(Func<HttpRequestMessage, Task<HttpResponseMessage>> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            body,
            request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join(";", cookie) : null,
            request.Headers.TryGetValues("Origin", out var origin) ? string.Join(";", origin) : null,
            request.Headers.TryGetValues("Referer", out var referer) ? string.Join(";", referer) : null));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("测试没有排够响应：请求次数比预期多。");
        }

        return await _responses.Dequeue()(request);
    }
}

/// <summary>
/// 一次被记下来的请求.
/// </summary>
/// <param name="Method">方法.</param>
/// <param name="Url">完整地址（只在测试里用，不会写进日志）.</param>
/// <param name="Body">正文.</param>
/// <param name="Cookie">Cookie 头.</param>
/// <param name="Origin">Origin 头.</param>
/// <param name="Referer">Referer 头.</param>
internal sealed record RecordedRequest(string Method, string Url, string? Body, string? Cookie, string? Origin, string? Referer);
