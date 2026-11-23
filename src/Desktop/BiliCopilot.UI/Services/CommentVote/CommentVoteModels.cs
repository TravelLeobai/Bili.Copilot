// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services;

/// <summary>
/// 一条评论里带的投票：只有从正文占位符抠出来的 id，标题与人数要等详情回来才有.
/// </summary>
/// <param name="Id">投票 id（十进制字符串）.</param>
/// <param name="Title">标题，未知时为 null.</param>
/// <param name="ParticipantCount">参与人数，未知时为 null.</param>
internal sealed record CommentVoteRef(string Id, string? Title = null, long? ParticipantCount = null);

/// <summary>
/// 投票的一个选项.
/// </summary>
/// <param name="Index">选项序号，服务端从 1 开始.</param>
/// <param name="Text">选项正文.</param>
/// <param name="Count">票数；服务端对 0 票的选项会省略 cnt 字段，这里补 0.</param>
/// <param name="Image">图片投票的配图，没有为 null.</param>
internal sealed record CommentVoteOption(int Index, string Text, long Count = 0, Uri? Image = null);

/// <summary>
/// 一个投票的完整信息.
/// </summary>
internal sealed class CommentVoteDetail
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteDetail"/> class.
    /// </summary>
    /// <param name="id">投票 id.</param>
    /// <param name="title">标题.</param>
    /// <param name="options">选项.</param>
    public CommentVoteDetail(string id, string title, IReadOnlyList<CommentVoteOption> options)
    {
        Id = id;
        Title = title;
        Options = options;
    }

    /// <summary>
    /// Gets 投票 id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets 标题.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets 发起人写的说明.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets 截止时间；没有截止时间（或时间远得没意义）时为 null.
    /// </summary>
    public DateTimeOffset? EndTime { get; init; }

    /// <summary>
    /// Gets 最多能选几项（已经按选项数钳过）.
    /// </summary>
    public int MaxChoiceCount { get; init; } = 1;

    /// <summary>
    /// Gets 参与人数.
    /// </summary>
    public long ParticipantCount { get; init; }

    /// <summary>
    /// Gets or sets 当前用户已经投过的选项序号；没投过就是空集合.
    /// </summary>
    public IReadOnlyList<int> MyVotes { get; set; } = [];

    /// <summary>
    /// Gets 发起人 id.
    /// </summary>
    public string? PublisherId { get; init; }

    /// <summary>
    /// Gets 发起人昵称.
    /// </summary>
    public string? PublisherName { get; init; }

    /// <summary>
    /// Gets a value indicating whether 只有舰长/充电用户可以投.
    /// </summary>
    public bool IsSupporterOnly { get; init; }

    /// <summary>
    /// Gets 选项.
    /// </summary>
    public IReadOnlyList<CommentVoteOption> Options { get; }

    /// <summary>
    /// Gets 总票数。多选投票的总票数会大于参与人数，占比按总票数算.
    /// </summary>
    public long TotalVotes => Options.Sum(option => Math.Max(0, option.Count));

    /// <summary>
    /// Gets a value indicating whether 投票已经截止.
    /// </summary>
    public bool HasEnded => EndTime is { } end && end <= DateTimeOffset.UtcNow;
}
