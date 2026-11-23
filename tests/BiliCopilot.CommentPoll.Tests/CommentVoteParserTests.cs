// Copyright (c) Bili Copilot. All rights reserved.

using System.Text.Json;
using BiliCopilot.UI.Services;
using Xunit;

namespace BiliCopilot.CommentPoll.Tests;

/// <summary>
/// vote_info 的解析。样本里第一条是真实响应.
/// </summary>
public class CommentVoteParserTests
{
    [Fact]
    public void RealResponse_IsParsedAsExpected()
    {
        var detail = Parse(VoteJson.RealDetail);

        Assert.NotNull(detail);
        Assert.Equal("20000000", detail!.Id);
        Assert.Equal("刚学的都还给老师了吗", detail.Title);
        Assert.Equal(4, detail.ParticipantCount);
        Assert.Equal(1, detail.MaxChoiceCount);
        Assert.Equal("85642211", detail.PublisherId);
        Assert.Equal("燕子不会离开", detail.PublisherName);
        Assert.False(detail.IsSupporterOnly);
        Assert.Empty(detail.MyVotes);
        Assert.NotNull(detail.EndTime);
        Assert.False(detail.HasEnded);

        Assert.Equal(2, detail.Options.Count);
        Assert.Equal(1, detail.Options[0].Index);
        Assert.Equal("全还回去了", detail.Options[0].Text);
        Assert.Equal(4, detail.Options[0].Count);

        // 0 票的选项服务端不给 cnt，必须补 0 而不是丢掉整个选项.
        Assert.Equal(2, detail.Options[1].Index);
        Assert.Equal("我能全文背诵", detail.Options[1].Text);
        Assert.Equal(0, detail.Options[1].Count);

        Assert.Equal(4, detail.TotalVotes);
    }

    [Fact]
    public void DeletedVote_IsUnavailable()
        => Assert.Null(Parse(VoteJson.DeletedDetail));

    [Fact]
    public void MissingOptions_IsUnavailable()
        => Assert.Null(Parse(VoteJson.NoOptions));

    [Fact]
    public void MissingData_IsUnavailable()
    {
        using var document = JsonDocument.Parse("""{"code":0,"data":null}""");
        Assert.Null(CommentVoteParser.ParseDetail(document.RootElement.GetProperty("data")));
    }

    [Fact]
    public void MyVotesAtDataLevel_AreRead()
    {
        var detail = Parse(VoteJson.MultiChoiceWithMyVotes);

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.MaxChoiceCount);
        Assert.Equal([1, 3], detail.MyVotes);
        Assert.Equal(10, detail.ParticipantCount);

        // 多选：总票数 6+5+0=11，可以大于参与人数 10.
        Assert.Equal(11, detail.TotalVotes);
    }

    [Fact]
    public void MyVotesInsideInfo_AreRead()
    {
        var detail = Parse(VoteJson.MyVotesInsideInfo);

        Assert.NotNull(detail);
        Assert.Equal([2], detail!.MyVotes);
    }

    [Fact]
    public void SupporterOnly_IsDetected()
    {
        var detail = Parse(VoteJson.MyVotesInsideInfo);

        Assert.NotNull(detail);
        Assert.True(detail!.IsSupporterOnly);
    }

    [Fact]
    public void EndedVote_IsDetected()
    {
        var detail = Parse(VoteJson.EndedDetail);

        Assert.NotNull(detail);
        Assert.True(detail!.HasEnded);
    }

    [Fact]
    public void DirtyFarEndTime_BecomesNull()
    {
        var detail = Parse(VoteJson.FarEndTime);

        Assert.NotNull(detail);
        Assert.Null(detail!.EndTime);
        Assert.False(detail.HasEnded);
    }

    [Fact]
    public void ChoiceCount_IsClampedToTheOptionCount()
    {
        // 只有 1 个选项却写了 choice_cnt=0：至少是 1.
        var detail = Parse(VoteJson.FarEndTime);
        Assert.Equal(1, detail!.MaxChoiceCount);

        // 3 个选项写了 choice_cnt=9：钳到 3.
        using var document = JsonDocument.Parse("""
            {"vote_info":{"vote_id":9,"title":"t","choice_cnt":9,"options":[{"opt_idx":1,"opt_desc":"a"},{"opt_idx":2,"opt_desc":"b"},{"opt_idx":3,"opt_desc":"c"}]}}
            """);
        Assert.Equal(3, CommentVoteParser.ParseDetail(document.RootElement)!.MaxChoiceCount);
    }

    [Fact]
    public void DuplicateAndNegativeMyVotes_AreDropped()
    {
        using var document = JsonDocument.Parse("""
            {"my_votes":[1,1,0,-2,2],"vote_info":{"vote_id":9,"title":"t","options":[{"opt_idx":1,"opt_desc":"a"},{"opt_idx":2,"opt_desc":"b"},{"opt_idx":3,"opt_desc":"c"}]}}
            """);
        var detail = CommentVoteParser.ParseDetail(document.RootElement);

        Assert.NotNull(detail);
        Assert.Equal([1, 2], detail!.MyVotes);
    }

    [Fact]
    public void NumbersAsStrings_AreAccepted()
    {
        using var document = JsonDocument.Parse("""
            {"vote_info":{"vote_id":"77","title":"文本数字","join_num":"12","choice_cnt":"1","end_time":"3929006537","options":[{"opt_idx":"1","opt_desc":"a","cnt":"3"}]}}
            """);
        var detail = CommentVoteParser.ParseDetail(document.RootElement);

        Assert.NotNull(detail);
        Assert.Equal("77", detail!.Id);
        Assert.Equal(12, detail.ParticipantCount);
        Assert.Equal(3, detail.Options[0].Count);
        Assert.NotNull(detail.EndTime);
    }

    [Fact]
    public void OptionImage_IsUpgradedToHttps()
    {
        using var document = JsonDocument.Parse("""
            {"vote_info":{"vote_id":9,"title":"图","options":[{"opt_idx":1,"opt_desc":"a","img_url":"http://i0.hdslb.com/a.png"},{"opt_idx":2,"opt_desc":"b","img_url":"//i0.hdslb.com/b.png"}]}}
            """);
        var detail = CommentVoteParser.ParseDetail(document.RootElement);

        Assert.NotNull(detail);
        Assert.Equal("https://i0.hdslb.com/a.png", detail!.Options[0].Image!.ToString());
        Assert.Equal("https://i0.hdslb.com/b.png", detail.Options[1].Image!.ToString());
    }

    [Fact]
    public void BlankTitle_IsUnavailable()
    {
        using var document = JsonDocument.Parse("""
            {"vote_info":{"vote_id":9,"title":"   ","options":[{"opt_idx":1,"opt_desc":"a"}]}}
            """);
        Assert.Null(CommentVoteParser.ParseDetail(document.RootElement));
    }

    [Fact]
    public void ParseOptions_OnResponseWithoutOptions_IsEmpty()
    {
        using var document = JsonDocument.Parse("""{"code":0,"data":{}}""");
        Assert.Empty(CommentVoteParser.ParseOptions(document.RootElement.GetProperty("data")));
    }

    /// <summary>
    /// 视频自己的投票：字段名和 vote_info 不是一套（vote_id/count/options[].idx、desc、count），
    /// 这里用 2026-11-23 从 av116367407716583 真实响应里抓到的片段做样本.
    /// </summary>
    [Fact]
    public void ParseVideoVoteCard_OnRealPayload_ReadsIdTitleAndCount()
    {
        var json = """
            {"code":0,"data":{"vote_card":{"vote_id":19089581,"title":"新三国谁是关羽","count":1060,
            "options":[{"idx":1,"desc":"没来那个","count":334},{"idx":2,"desc":"孙乾","count":279},
            {"idx":3,"desc":"周仓","count":217},{"idx":4,"desc":"吕蒙","count":230}]}}}
            """;

        var vote = CommentVoteParser.ParseVideoVoteCard(json);

        Assert.NotNull(vote);
        Assert.Equal("19089581", vote!.Id);
        Assert.Equal("新三国谁是关羽", vote.Title);
        Assert.Equal(1060, vote.ParticipantCount);
    }

    [Fact]
    public void ParseVideoVoteCard_WithoutVoteCard_IsNull()
    {
        Assert.Null(CommentVoteParser.ParseVideoVoteCard("""{"code":0,"data":{"vote":0}}"""));
    }

    [Fact]
    public void ParseVideoVoteCard_WithNoOptions_IsNull()
    {
        Assert.Null(CommentVoteParser.ParseVideoVoteCard("""{"code":0,"data":{"vote_card":{"vote_id":7,"title":"x","options":[]}}}"""));
    }

    [Fact]
    public void ParseVideoVoteCard_OnBrokenJson_IsNull()
    {
        Assert.Null(CommentVoteParser.ParseVideoVoteCard("{not json"));
    }

    private static CommentVoteDetail? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CommentVoteParser.ParseDetail(document.RootElement.GetProperty("data"));
    }
}
