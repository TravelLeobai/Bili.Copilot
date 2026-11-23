// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Services;
using Xunit;

namespace BiliCopilot.CommentPoll.Tests;

/// <summary>
/// 投票服务：查详情、投票、缓存与错误分类。
/// <para>
/// 全部请求都走 <see cref="FakeHttpHandler"/>：不发真实请求，也绝不投真实票.
/// </para>
/// </summary>
public class CommentVoteServiceTests
{
    private const string Cookie = "SESSDATA=abc; bili_jct=deadbeef; DedeUserID=123";
    private const string VoterUid = "123";

    [Fact]
    public async Task GetDetail_RealResponse_IsParsed()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, VoteJson.RealDetail);

        var detail = await service.GetDetailAsync("20000000", Cookie, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("20000000", detail!.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Contains("vote_id=20000000", request.Url, StringComparison.Ordinal);
        Assert.Equal(Cookie, request.Cookie);
    }

    [Fact]
    public async Task GetDetail_DeletedVote_IsNull()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, VoteJson.DeletedDetail);

        Assert.Null(await service.GetDetailAsync("123456789", Cookie, CancellationToken.None));
    }

    [Fact]
    public async Task GetDetail_NotDisplayableCode_IsNull()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"code":260010,"message":"不能看"}""");

        Assert.Null(await service.GetDetailAsync("999", Cookie, CancellationToken.None));
    }

    [Fact]
    public async Task GetDetail_ServerCode_IsRejected()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, VoteJson.Rejected);

        var ex = await Assert.ThrowsAsync<CommentVoteRejectedException>(
            () => service.GetDetailAsync("20000000", Cookie, CancellationToken.None));
        Assert.Equal(500001, ex.Code);
        Assert.Equal("投票已结束", ex.Reason);
    }

    [Fact]
    public async Task GetDetail_HttpFailure_Throws_NotUnavailable()
    {
        // 加载失败 ≠ 投票不存在：这条必须是异常，不能悄悄返回 null.
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetDetailAsync("20000000", Cookie, CancellationToken.None));
    }

    [Fact]
    public async Task GetDetail_Http412_IsForbidden_AndNotRetried()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.PreconditionFailed, VoteJson.RiskControl);

        await Assert.ThrowsAsync<CommentVoteForbiddenException>(
            () => service.GetDetailAsync("20000000", Cookie, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task GetDetail_InvalidId_IsUnavailable_WithoutRequest(string voteId)
    {
        var (service, handler) = Create();

        Assert.Null(await service.GetDetailAsync(voteId, Cookie, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetDetail_SecondCall_ComesFromCache()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, VoteJson.RealDetail);

        var first = await service.GetDetailAsync("20000000", Cookie, CancellationToken.None);
        var second = await service.GetDetailAsync("20000000", Cookie, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetDetail_SameVoteAtTheSameTime_IsFetchedOnce()
    {
        var (service, handler) = Create();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler
            .Enqueue(async _ =>
            {
                await gate.Task;
                return Json(VoteJson.RealDetail);
            })
            .Enqueue(_ => Task.FromResult(Json(VoteJson.RealDetail)));

        var first = service.GetDetailAsync("20000000", Cookie, CancellationToken.None);
        var second = service.GetDetailAsync("20000000", Cookie, CancellationToken.None);
        await WaitUntilAsync(() => handler.Requests.Count == 1);
        gate.SetResult();

        var a = await first;
        var b = await second;

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetDetail_CacheKeepsAtMost64Entries()
    {
        var (service, handler) = Create();
        for (var i = 0; i < 65; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, VoteJson.RealDetail);
        }

        handler.Enqueue(HttpStatusCode.OK, VoteJson.RealDetail);
        for (var i = 1; i <= 65; i++)
        {
            Assert.NotNull(await service.GetDetailAsync(i.ToString(System.Globalization.CultureInfo.InvariantCulture), Cookie, CancellationToken.None));
        }

        // 第 1 条已经被挤出去了，再问就要重新请求.
        Assert.NotNull(await service.GetDetailAsync("1", Cookie, CancellationToken.None));
        Assert.Equal(66, handler.Requests.Count);
    }

    [Fact]
    public async Task Submit_WithNewCounts_MergesThem()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, VoteJson.SubmitWithOptions);
        var detail = RealVote();

        var result = await service.SubmitAsync(detail, [2], VoterUid, anonymous: false, Cookie, CancellationToken.None);

        // 标题等信息保留旧的，票数用响应里的.
        Assert.Equal("刚学的都还给老师了吗", result.Title);
        Assert.Equal(5, result.Options[0].Count);
        Assert.Equal(1, result.Options[1].Count);
        Assert.Equal([2], result.MyVotes);
        Assert.Equal(6, result.ParticipantCount);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Contains("csrf=deadbeef", request.Url, StringComparison.Ordinal);
        Assert.Equal("""{"vote_id":20000000,"votes":[2],"voter_uid":123,"status":0,"op_bit":0,"dynamic_id":0}""", request.Body);
        Assert.Equal("https://t.bilibili.com", request.Origin);
        Assert.Equal("https://t.bilibili.com/vote/h5/index/", request.Referer);
        Assert.Equal(Cookie, request.Cookie);
    }

    [Fact]
    public async Task Submit_Anonymous_SendsStatus1()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, VoteJson.SubmitWithOptions);

        await service.SubmitAsync(RealVote(), [1], VoterUid, anonymous: true, Cookie, CancellationToken.None);

        Assert.Contains("\"status\":1", Assert.Single(handler.Requests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_WithoutCounts_RequeriesTheDetail()
    {
        var (service, handler) = Create();
        handler
            .Enqueue(HttpStatusCode.OK, VoteJson.SubmitWithoutOptions)
            .Enqueue(HttpStatusCode.OK, VoteJson.RefreshedDetail);

        var result = await service.SubmitAsync(RealVote(), [2], VoterUid, anonymous: false, Cookie, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Equal(4, result.Options[0].Count);
        Assert.Equal(1, result.Options[1].Count);
        Assert.Equal(5, result.ParticipantCount);
        Assert.Equal([2], result.MyVotes);
    }

    [Fact]
    public async Task Submit_RequeryFails_CountsLocally()
    {
        var (service, handler) = Create();
        handler
            .Enqueue(HttpStatusCode.OK, VoteJson.SubmitWithoutOptions)
            .Enqueue(HttpStatusCode.InternalServerError, "{}");

        var result = await service.SubmitAsync(RealVote(), [2], VoterUid, anonymous: false, Cookie, CancellationToken.None);

        // 票已经投出去了，只是结果没读到：本地 +1，不能报失败.
        Assert.Equal(4, result.Options[0].Count);
        Assert.Equal(1, result.Options[1].Count);
        Assert.Equal(5, result.ParticipantCount);
        Assert.Equal([2], result.MyVotes);
    }

    [Fact]
    public async Task Submit_ResponseIsNotJson_IsTreatedAsSuccess()
    {
        var (service, handler) = Create();
        handler
            .Enqueue(HttpStatusCode.OK, VoteJson.NotJson)
            .Enqueue(HttpStatusCode.InternalServerError, "{}");

        var result = await service.SubmitAsync(RealVote(), [1], VoterUid, anonymous: false, Cookie, CancellationToken.None);

        Assert.Equal(5, result.Options[0].Count);
        Assert.Equal(5, result.ParticipantCount);
    }

    [Theory]
    [InlineData(VoteJson.NotSignedIn)]
    [InlineData(VoteJson.VoteNotSignedIn)]
    public async Task Submit_NotSignedIn_IsUnauthorized(string body)
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SubmitAsync(RealVote(), [1], VoterUid, anonymous: false, Cookie, CancellationToken.None));

        // 不重试：就发这一次.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Submit_OtherCode_IsRejectedWithReason()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, VoteJson.Rejected);

        var ex = await Assert.ThrowsAsync<CommentVoteRejectedException>(
            () => service.SubmitAsync(RealVote(), [1], VoterUid, anonymous: false, Cookie, CancellationToken.None));

        Assert.Equal(500001, ex.Code);
        Assert.Equal("投票已结束", ex.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Submit_Http412_IsForbidden_AndSentOnlyOnce()
    {
        var (service, handler) = Create();
        handler.Enqueue(HttpStatusCode.PreconditionFailed, VoteJson.RiskControl);

        await Assert.ThrowsAsync<CommentVoteForbiddenException>(
            () => service.SubmitAsync(RealVote(), [1], VoterUid, anonymous: false, Cookie, CancellationToken.None));

        // 关键断言：412 之后一个字节都不能再发.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Submit_WithoutCsrf_IsUnauthorized_WithoutRequest()
    {
        var (service, handler) = Create();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SubmitAsync(RealVote(), [1], VoterUid, anonymous: false, "SESSDATA=abc", CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Submit_WithoutVoterId_IsUnauthorized_WithoutRequest()
    {
        var (service, handler) = Create();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SubmitAsync(RealVote(), [1], string.Empty, anonymous: false, Cookie, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Submit_UnknownOption_IsRejectedBeforeSending()
    {
        var (service, handler) = Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SubmitAsync(RealVote(), [7], VoterUid, anonymous: false, Cookie, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Submit_TooManyChoices_IsRejectedBeforeSending()
    {
        var (service, handler) = Create();

        // 单选投票不能一次投两项.
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SubmitAsync(RealVote(), [1, 2], VoterUid, anonymous: false, Cookie, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Submit_InvalidatesTheCachedDetail()
    {
        var (service, handler) = Create();
        handler
            .Enqueue(HttpStatusCode.OK, VoteJson.RealDetail)
            .Enqueue(HttpStatusCode.OK, VoteJson.SubmitWithOptions)
            .Enqueue(HttpStatusCode.OK, VoteJson.RefreshedDetail);
        var detail = RealVote();

        var cached = await service.GetDetailAsync("20000000", Cookie, CancellationToken.None);
        Assert.NotNull(cached);
        await service.SubmitAsync(cached!, [2], VoterUid, anonymous: false, Cookie, CancellationToken.None);

        // 提交成功后缓存要被丢掉，下一次必须重新拉.
        var again = await service.GetDetailAsync("20000000", Cookie, CancellationToken.None);

        Assert.NotNull(again);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(5, again!.ParticipantCount);
    }

    private static (CommentVoteService Service, FakeHttpHandler Handler) Create()
    {
        var handler = new FakeHttpHandler();
        var service = new CommentVoteService(handler, TimeSpan.Zero);
        return (service, handler);
    }

    private static CommentVoteDetail RealVote()
    {
        using var document = JsonDocument.Parse(VoteJson.RealDetail);
        return CommentVoteParser.ParseDetail(document.RootElement.GetProperty("data"))!;
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("等待条件超时。");
    }
}
