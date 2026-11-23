// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services;
using Xunit;

namespace BiliCopilot.CommentPoll.Tests;

/// <summary>
/// 正文里的 <c>{vote:ID}</c>：抠 id 与剥正文.
/// </summary>
public class CommentVoteTextTests
{
    [Fact]
    public void PlainText_HasNoVote_AndIsNotTrimmed()
    {
        const string Text = "  今天的饭真好吃  ";

        Assert.False(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal(string.Empty, id);

        // 没有占位符就必须原样返回：连首尾空格都不许动（"不要无脑 Trim"）.
        Assert.Same(Text, CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void LeadingMarker_IsExtractedAndStripped()
    {
        const string Text = "{vote:123}你好";

        Assert.True(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal("123", id);
        Assert.Equal("你好", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void TrailingMarker_IsExtractedAndStripped()
    {
        const string Text = "你好{vote:123}";

        Assert.True(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal("123", id);
        Assert.Equal("你好", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void MarkerInTheMiddle_LeavesTheRestIntact()
    {
        const string Text = "前面{vote:9527}后面";

        Assert.True(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal("9527", id);
        Assert.Equal("前面后面", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void EmotesMixedWithVote_AreKept()
    {
        const string Text = "[微笑]{vote:456}哈哈[大哭]";

        Assert.True(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal("456", id);
        Assert.Equal("[微笑]哈哈[大哭]", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void MultipleMarkers_UseTheFirstId_AndAllAreStripped()
    {
        const string Text = "{vote:1}{vote:2}看这个";

        Assert.True(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal("1", id);
        Assert.Equal("看这个", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void EmptyId_IsNotAUsableVote_ButTheMarkerIsStripped()
    {
        const string Text = "{vote:}没有 id";

        Assert.False(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal(string.Empty, id);
        Assert.Equal("没有 id", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void NonNumericId_IsNotAUsableVote_ButTheMarkerIsStripped()
    {
        const string Text = "{vote:abc}非法";

        Assert.False(CommentVoteText.TryExtractVoteId(Text, out _));
        Assert.Equal("非法", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void OversizedId_IsNotAUsableVote_ButTheMarkerIsStripped()
    {
        // 超出 long 的数字：解析不出来就当不可用，绝对不能抛异常.
        const string Text = "{vote:99999999999999999999999}超大";

        Assert.False(CommentVoteText.TryExtractVoteId(Text, out _));
        Assert.Equal("超大", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void ZeroId_IsNotAUsableVote()
    {
        const string Text = "{vote:0}零";

        Assert.False(CommentVoteText.TryExtractVoteId(Text, out _));
        Assert.Equal("零", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void UppercaseVariant_IsRecognized()
    {
        const string Text = "{VOTE:789}大写";

        Assert.True(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal("789", id);
        Assert.Equal("大写", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void WhitespaceInsideTheBraces_IsRecognized()
    {
        const string Text = "{ vote : 654 }带空格";

        Assert.True(CommentVoteText.TryExtractVoteId(Text, out var id));
        Assert.Equal("654", id);
        Assert.Equal("带空格", CommentVoteText.StripVotePlaceholders(Text));
    }

    [Fact]
    public void NormalizesLeadingZeros()
    {
        Assert.True(CommentVoteText.TryExtractVoteId("{vote:000123}", out var id));
        Assert.Equal("123", id);
    }

    [Fact]
    public void NullAndEmpty_AreSafe()
    {
        Assert.False(CommentVoteText.TryExtractVoteId(null, out _));
        Assert.False(CommentVoteText.TryExtractVoteId(string.Empty, out _));
        Assert.False(CommentVoteText.HasVotePlaceholder(null));
        Assert.Equal(string.Empty, CommentVoteText.StripVotePlaceholders(null));
    }

    [Theory]
    [InlineData("{vote:123}有")]
    [InlineData("{vote:}有")]
    [InlineData("{vote:abc}有")]
    public void HasPlaceholder_CountsInvalidMarkersToo(string text)
        => Assert.True(CommentVoteText.HasVotePlaceholder(text));
}
