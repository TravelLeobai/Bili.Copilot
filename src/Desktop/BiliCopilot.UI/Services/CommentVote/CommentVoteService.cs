// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 评论里带的投票：查详情、投票.
/// <para>
/// 投票和动态里的是同一套，接口跟官方投票页走：GET <c>/x/vote/vote_info</c>，
/// POST <c>/x/vote/do_vote</c>（JSON body，csrf 在 query 里）.
/// </para>
/// <para>
/// 这里只管请求与解析，不碰 UI。「投票不存在」用 null 表示，「请求失败」用异常表示，
/// 两者必须能分开 —— 评论列表里把一个网络抖动说成「投票已删除」是不可接受的.
/// </para>
/// </summary>
internal sealed class CommentVoteService
{
    private const string VoteInfoApi = "https://api.bilibili.com/x/vote/vote_info";
    private const string DoVoteApi = "https://api.bilibili.com/x/vote/do_vote";

    // 评论首屏：视频自己的投票（data.vote_card）只在这里出现.
    private const string CommentMainApi = "https://api.bilibili.com/x/v2/reply/main";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    // 服务端对「存在但不给看」的投票回的码.
    private const int NotDisplayableCode = 260010;

    // 通用「没登录」码，和投票接口自己用的那个.
    private const int NotSignedInCode = -101;
    private const int VoteNotSignedInCode = 100;

    // 详情缓存：同一个投票可能在很多条评论里出现，别各拉一遍.
    private const int MaxCachedDetails = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(3);

    // 同一时刻最多几个详情请求，其余排队；一次展开很多条评论时不至于把接口打爆.
    private const int MaxConcurrentDetailRequests = 3;

    // 投票成功但响应没带票数时，官方页面也是这个间隔重查一次.
    private static readonly TimeSpan DefaultRequeryDelay = TimeSpan.FromMilliseconds(600);

    private const int MaxCachedVideoVotes = 32;
    private static readonly TimeSpan VideoVoteCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly HttpClient _client;
    private readonly TimeSpan _requeryDelay;
    private readonly ILogger<CommentVoteService> _logger;
    private readonly SemaphoreSlim _detailGate = new(MaxConcurrentDetailRequests, MaxConcurrentDetailRequests);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VideoVoteCacheEntry> _videoVoteCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<CommentVoteDetail?>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteService"/> class.
    /// </summary>
    /// <param name="handler">HTTP handler（测试用假 handler；正式运行传 null）.</param>
    /// <param name="requeryDelay">投票后重查详情的间隔（测试里传 Zero，免得白等 600ms）.</param>
    /// <param name="logger">日志.</param>
    public CommentVoteService(
        HttpMessageHandler? handler = null,
        TimeSpan? requeryDelay = null,
        ILogger<CommentVoteService>? logger = null)
    {
        _client = handler is null
            ? new HttpClient(CreateHandler()) { Timeout = TimeSpan.FromSeconds(12) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _requeryDelay = requeryDelay ?? DefaultRequeryDelay;
        _logger = logger ?? NullLogger<CommentVoteService>.Instance;
    }

    /// <summary>
    /// Gets 应用里共用的那一个（缓存与并发闸门都在实例上，不能每处 new 一个）.
    /// </summary>
    public static CommentVoteService Default { get; } = new();

    /// <summary>
    /// 查投票详情.
    /// </summary>
    /// <param name="voteId">投票 id.</param>
    /// <param name="cookie">登录态；为空也能查，只是拿不到自己投了什么.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>投票；不可用（已删除、id 非法、不给看）时返回 null；请求失败则抛异常.</returns>
    public async Task<CommentVoteDetail?> GetDetailAsync(string voteId, string cookie, CancellationToken cancellationToken)
    {
        if (!TryNormalizeId(voteId, out var id))
        {
            return null;
        }

        if (TryGetCached(id, out var cached))
        {
            return cached;
        }

        return await GetOrFetchAsync(id, cookie ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 投票.
    /// </summary>
    /// <param name="detail">当前展示的投票（投票后要拿它当底稿合并新票数）.</param>
    /// <param name="optionIndexes">选中的选项序号.</param>
    /// <param name="voterUid">投票人 uid.</param>
    /// <param name="anonymous">是否匿名.</param>
    /// <param name="cookie">登录态（要从中取 bili_jct 作 csrf）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>投票之后的详情.</returns>
    /// <exception cref="UnauthorizedAccessException">没登录 / 没有 csrf.</exception>
    /// <exception cref="CommentVoteRejectedException">服务端拒绝（带 code 与原因）.</exception>
    /// <exception cref="CommentVoteForbiddenException">被风控挡下（HTTP 412），不重试.</exception>
    public async Task<CommentVoteDetail> SubmitAsync(
        CommentVoteDetail detail,
        IReadOnlyList<int> optionIndexes,
        string voterUid,
        bool anonymous,
        string cookie,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(optionIndexes);

        var votes = optionIndexes.Distinct().ToList();
        if (votes.Count == 0
            || votes.Count > detail.MaxChoiceCount
            || votes.Any(index => detail.Options.All(option => option.Index != index)))
        {
            throw new ArgumentException("选中的选项不属于这个投票。", nameof(optionIndexes));
        }

        if (!long.TryParse(voterUid, NumberStyles.None, CultureInfo.InvariantCulture, out var voter) || voter <= 0)
        {
            throw new UnauthorizedAccessException("投票需要先登录。");
        }

        var csrf = ExtractCookieValue(cookie, "bili_jct");
        if (string.IsNullOrEmpty(csrf))
        {
            throw new UnauthorizedAccessException("投票需要先登录。");
        }

        var body = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"vote_id\":{0},\"votes\":[{1}],\"voter_uid\":{2},\"status\":{3},\"op_bit\":0,\"dynamic_id\":0}}",
            detail.Id,
            string.Join(",", votes.Select(index => index.ToString(CultureInfo.InvariantCulture))),
            voter,
            anonymous ? 1 : 0);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            DoVoteApi + "?csrf=" + Uri.EscapeDataString(csrf))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddCommonHeaders(request, cookie, "https://t.bilibili.com/vote/h5/index/");
        request.Headers.TryAddWithoutValidation("Origin", "https://t.bilibili.com");

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // 412 是被风控挡了：再发一次只会继续被挡，所以单独认出来并且绝不重试.
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            _logger.LogWarning("评论投票被风控挡下（HTTP 412），不重试：vote={VoteId}", detail.Id);
            throw new CommentVoteForbiddenException("投票请求被平台拦截，请稍后再试。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"投票请求失败（HTTP {(int)response.StatusCode}）。");
        }

        var code = 0;
        string? reason = null;
        IReadOnlyList<CommentVoteOption> options = [];
        IReadOnlyList<int> myVotes = [];
        long participants = 0;
        var parsed = true;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            code = (int)CommentVoteParser.GetInt64(root, "code");
            reason = CommentVoteParser.GetString(root, "message") ?? CommentVoteParser.GetString(root, "msg");
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                var info = data;
                if (data.TryGetProperty("vote_info", out var voteInfo) && voteInfo.ValueKind == JsonValueKind.Object)
                {
                    info = voteInfo;
                }

                options = CommentVoteParser.ParseOptions(info);
                myVotes = CommentVoteParser.ParseIndexes(info, "my_votes");
                if (myVotes.Count == 0)
                {
                    myVotes = CommentVoteParser.ParseIndexes(data, "my_votes");
                }

                participants = CommentVoteParser.GetInt64(info, "join_num");
            }
        }
        catch (JsonException)
        {
            // 响应不是 JSON：票其实已经投出去了，只是结果读不到，按成功处理（下面走重查）.
            parsed = false;
        }

        if (parsed)
        {
            if (code is NotSignedInCode or VoteNotSignedInCode)
            {
                throw new UnauthorizedAccessException("投票需要先登录。");
            }

            if (code != 0)
            {
                throw new CommentVoteRejectedException(code, reason);
            }
        }

        // 票已经投出去了，缓存里的旧票数一律作废.
        Invalidate(detail.Id);

        if (options.Count > 0)
        {
            // 响应带了新票数：只取票数，标题等信息还是用旧的.
            return Merge(
                detail,
                options,
                participants > 0 ? participants : detail.ParticipantCount + 1,
                myVotes.Count > 0 ? myVotes : votes);
        }

        // 没带票数：等一会儿重查一次（官方页面也是这么干的）.
        try
        {
            await Task.Delay(_requeryDelay, cancellationToken).ConfigureAwait(false);
            var refreshed = await FetchDetailAsync(detail.Id, cookie, CancellationToken.None).ConfigureAwait(false);
            if (refreshed is not null)
            {
                if (refreshed.MyVotes.Count == 0)
                {
                    refreshed.MyVotes = votes;
                }

                Remember(detail.Id, refreshed);
                return refreshed;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 投票本身成功了，只是结果没读到：落到下面本地 +1，不能报「投票失败」.
            _logger.LogDebug("投票后重查详情失败（{ErrorType}），改为本地 +1：vote={VoteId}", ex.GetType().Name, detail.Id);
        }

        return Merge(
            detail,
            detail.Options
                .Select(option => votes.Contains(option.Index) ? option with { Count = option.Count + 1 } : option)
                .ToList(),
            detail.ParticipantCount + 1,
            votes);
    }

    /// <summary>
    /// 查「视频自己的投票」——它不在评论区任何一条评论里，而是评论首屏接口 <c>data.vote_card</c> 带下来的。
    /// 只有视频评论有，动态/番剧没有；没有投票时返回 null.
    /// </summary>
    /// <param name="oid">评论对象 id（视频是 aid）.</param>
    /// <param name="targetType">评论类型（视频 1）.</param>
    /// <param name="cookie">登录态（可选）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>投票引用；没有视频投票时 null.</returns>
    public async Task<CommentVoteRef?> GetVideoVoteCardAsync(string oid, int targetType, string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(oid) || targetType <= 0)
        {
            return null;
        }

        var key = $"{targetType}:{oid}";
        if (_videoVoteCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt <= VideoVoteCacheLifetime)
        {
            return cached.Vote;
        }

        CommentVoteRef? vote = null;
        try
        {
            var url = $"{CommentMainApi}?type={targetType}&oid={Uri.EscapeDataString(oid)}&mode=3&next=0&ps=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request, cookie);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                vote = CommentVoteParser.ParseVideoVoteCard(json);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 拿不到就当没有：评论区不该因为一个可选卡片打不开而报错.
            _logger.LogDebug("视频投票卡片读取失败：{ErrorType}", ex.GetType().Name);
            return null;
        }

        if (_videoVoteCache.Count > MaxCachedVideoVotes)
        {
            foreach (var pair in _videoVoteCache.OrderBy(pair => pair.Value.FetchedAt).Take(_videoVoteCache.Count - MaxCachedVideoVotes))
            {
                _videoVoteCache.TryRemove(pair.Key, out _);
            }
        }

        _videoVoteCache[key] = new VideoVoteCacheEntry(vote, DateTimeOffset.UtcNow);
        if (vote is not null)
        {
            _logger.LogInformation("视频投票：vote={VoteId} 标题={Title}", vote.Id, vote.Title);
        }

        return vote;
    }

    private static SocketsHttpHandler CreateHandler()
        => new()
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };

    private static bool TryNormalizeId(string? voteId, out string id)
    {
        id = string.Empty;
        if (!long.TryParse(voteId, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return false;
        }

        id = value.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static string? ExtractCookieValue(string? cookie, string name)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        foreach (var part in cookie.Split(';'))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            if (part[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    // 只保留描述性字段，票数与「我投了哪几项」取新的.
    private static CommentVoteDetail Merge(
        CommentVoteDetail vote,
        IReadOnlyList<CommentVoteOption> results,
        long participantCount,
        IReadOnlyList<int> myVotes)
    {
        var options = vote.Options
            .Select(option => results.FirstOrDefault(result => result.Index == option.Index) is { } result
                ? new CommentVoteOption(option.Index, option.Text, result.Count, option.Image)
                : option)
            .ToList();

        return new CommentVoteDetail(vote.Id, vote.Title, options)
        {
            Description = vote.Description,
            EndTime = vote.EndTime,
            MaxChoiceCount = vote.MaxChoiceCount,
            ParticipantCount = participantCount,
            MyVotes = myVotes,
            PublisherId = vote.PublisherId,
            PublisherName = vote.PublisherName,
            IsSupporterOnly = vote.IsSupporterOnly,
        };
    }

    private async Task<CommentVoteDetail?> GetOrFetchAsync(string id, string cookie, CancellationToken cancellationToken)
    {
        while (true)
        {
            // 同一条投票已经有人在拉：等它的结果，别再发一遍.
            if (_inFlight.TryGetValue(id, out var running))
            {
                return await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var completion = new TaskCompletionSource<CommentVoteDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inFlight.TryAdd(id, completion.Task))
            {
                continue;
            }

            try
            {
                // 请求本身故意不绑定某一个调用方的取消令牌：有人关掉弹出之后，
                // 排在同一投票后面的评论还等着这份结果.
                var detail = await FetchDetailAsync(id, cookie, CancellationToken.None).ConfigureAwait(false);
                if (detail is not null)
                {
                    Remember(id, detail);
                }

                completion.SetResult(detail);
                return detail;
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
                throw;
            }
            finally
            {
                _inFlight.TryRemove(id, out _);
            }
        }
    }

    private async Task<CommentVoteDetail?> FetchDetailAsync(string id, string cookie, CancellationToken cancellationToken)
    {
        await _detailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{VoteInfoApi}?vote_id={id}");
            AddCommonHeaders(request, cookie);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                _logger.LogWarning("评论投票详情被风控挡下（HTTP 412），不重试：vote={VoteId}", id);
                throw new CommentVoteForbiddenException("投票详情请求被平台拦截，请稍后再试。");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"投票详情请求失败（HTTP {(int)response.StatusCode}）。");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = (int)CommentVoteParser.GetInt64(root, "code");
            if (code != 0)
            {
                if (code == NotDisplayableCode)
                {
                    // 存在但不给看，跟没有一样.
                    return null;
                }

                var reason = CommentVoteParser.GetString(root, "message") ?? CommentVoteParser.GetString(root, "msg");
                throw new CommentVoteRejectedException(code, reason);
            }

            return root.TryGetProperty("data", out var data) ? CommentVoteParser.ParseDetail(data) : null;
        }
        finally
        {
            _detailGate.Release();
        }
    }

    private static void AddCommonHeaders(HttpRequestMessage request, string? cookie, string referer = "https://www.bilibili.com")
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
    }

    private bool TryGetCached(string id, out CommentVoteDetail? detail)
    {
        detail = null;
        if (!_cache.TryGetValue(id, out var entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.FetchedAt > CacheLifetime)
        {
            _cache.TryRemove(id, out _);
            return false;
        }

        detail = entry.Detail;
        return true;
    }

    private void Remember(string id, CommentVoteDetail detail)
    {
        _cache[id] = new CacheEntry(detail, DateTimeOffset.UtcNow);
        if (_cache.Count <= MaxCachedDetails)
        {
            return;
        }

        // 超容量就丢最旧的一条，缓存不能无界增长.
        var oldest = _cache.OrderBy(pair => pair.Value.FetchedAt).First();
        _cache.TryRemove(oldest.Key, out _);
    }

    private void Invalidate(string id)
    {
        _cache.TryRemove(id, out _);
        _logger.LogDebug("投票详情缓存已失效：vote={VoteId}", id);
    }

    private readonly record struct CacheEntry(CommentVoteDetail Detail, DateTimeOffset FetchedAt);

    private readonly record struct VideoVoteCacheEntry(CommentVoteRef? Vote, DateTimeOffset FetchedAt);
}
