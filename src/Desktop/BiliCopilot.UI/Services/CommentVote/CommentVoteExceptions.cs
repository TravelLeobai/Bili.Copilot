// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services;

/// <summary>
/// 服务端收到投票但拒绝了（已经投过、投票已关闭、只给舰长……）.
/// </summary>
internal sealed class CommentVoteRejectedException : Exception
{
    // 服务端用这个码表示「只有充电/舰长能投」.
    private const int SupporterOnlyCode = 5200011;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteRejectedException"/> class.
    /// </summary>
    public CommentVoteRejectedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteRejectedException"/> class.
    /// </summary>
    /// <param name="message">说明.</param>
    public CommentVoteRejectedException(string? message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteRejectedException"/> class.
    /// </summary>
    /// <param name="message">说明.</param>
    /// <param name="innerException">原因.</param>
    public CommentVoteRejectedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteRejectedException"/> class.
    /// </summary>
    /// <param name="code">服务端返回的 code.</param>
    /// <param name="reason">服务端给的原因，可能为空.</param>
    public CommentVoteRejectedException(int code, string? reason)
        : base(string.IsNullOrWhiteSpace(reason) ? $"投票被拒绝（code {code}）." : reason)
    {
        Code = code;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason!.Trim();
    }

    /// <summary>
    /// Gets 服务端返回的 code.
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// Gets 服务端给的原因；没给就是 null.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Gets a value indicating whether 是因为「只有充电/舰长能投」被拒.
    /// </summary>
    public bool IsSupporterOnly => Code == SupporterOnlyCode;
}

/// <summary>
/// 请求被风控挡下来了（HTTP 412）.
/// <para>
/// 这种情况重试只会继续被挡，所以调用方必须能认出来并且不要自动重试.
/// </para>
/// </summary>
internal sealed class CommentVoteForbiddenException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteForbiddenException"/> class.
    /// </summary>
    public CommentVoteForbiddenException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteForbiddenException"/> class.
    /// </summary>
    /// <param name="message">说明.</param>
    public CommentVoteForbiddenException(string? message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteForbiddenException"/> class.
    /// </summary>
    /// <param name="message">说明.</param>
    /// <param name="innerException">原因.</param>
    public CommentVoteForbiddenException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
