// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using BiliCopilot.UI.Toolkits;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Bili.User;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 直播分区详情视图模型。
/// 上半部分是关注 UP 主的过滤栏，下面是"正在直播"的卡片，
/// 再往下是关注 UP 主视频里标题带"直播回放 / 录播"的内容。
/// </summary>
public sealed partial class LiveMomentSectionDetailViewModel : LayoutPageViewModelBase, IMomentSectionDetailViewModel
{
    private const string SearchApi = "https://api.bilibili.com/x/web-interface/search/type?search_type=video&order=pubdate&page=1&keyword={0}";

    /// <summary>
    /// 每次往界面补多少张卡片.
    /// </summary>
    private const int PageBatchSize = 12;

    /// <summary>
    /// 标题里出现这些词就当成录播。
    /// 「录像」放最后：它最宽，先让「直播录像 / 直播回放 / 录播」这些更具体的词命中.
    /// </summary>
    private static readonly string[] ReplayKeywords = ["直播回放", "直播录像", "录播", "回放", "录像"];

    private readonly ILogger<LiveMomentSectionDetailViewModel> _logger;
    private readonly List<LiveSectionCardViewModel> _allCards = [];

    /// <summary>
    /// 界面上最多铺多少张（滚到底再按批增长）.
    /// </summary>
    private int _visibleCount = PageBatchSize;

    private readonly Dictionary<string, string> _followedNames = [];
    private readonly Dictionary<string, Uri?> _followedAvatars = [];

    private readonly IMomentDiscoveryService? _momentService;

    /// <summary>
    /// 上次按开播状态核对卡片的时间：进页面时受冷却限制，避免来回切页把接口打爆.
    /// </summary>
    private DateTimeOffset _lastPruneAt = DateTimeOffset.MinValue;

    /// <summary>
    /// 进页面自动核对的冷却时长；手动刷新不受这个限制.
    /// </summary>
    private static readonly TimeSpan PruneCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentSectionDetailViewModel"/> class.
    /// </summary>
    /// <param name="momentService">动态服务（用来拿关注 UP 主的投稿）.</param>
    public LiveMomentSectionDetailViewModel(IMomentDiscoveryService momentService)
    {
        _momentService = momentService;
        _logger = this.Get<ILogger<LiveMomentSectionDetailViewModel>>();
    }

    /// <inheritdoc/>
    public MomentSectionType SectionType => MomentSectionType.Live;

    /// <summary>
    /// 顶部 UP 主过滤项.
    /// </summary>
    public ObservableCollection<LiveSectionUperViewModel> Upers { get; } = [];

    /// <summary>
    /// 过滤后要展示的卡片.
    /// </summary>
    public ObservableCollection<LiveSectionCardViewModel> Items { get; } = [];

    /// <summary>
    /// 内容类型过滤：全部 / 直播 / 回放.
    /// </summary>
    [ObservableProperty]
    private LiveFilterType _filterType = LiveFilterType.All;

    [ObservableProperty]
    private LiveSectionUperViewModel? _selectedUper;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    /// <inheritdoc/>
    protected override string GetPageKey() => "LiveMoment";

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (_allCards.Count > 0)
        {
            return;
        }

        // 有缓存就先把上次的卡片铺出来（切走再切回来不该白等一轮网络），
        // 内容过期了再在后台补新的.
        await Services.LiveSectionCache.EnsureLoadedAsync();
        if (Services.LiveSectionCache.HasContent)
        {
            _allCards.AddRange(Services.LiveSectionCache.GetCards());
            BuildUpers(_allCards);
            SelectedUper ??= Upers.FirstOrDefault();
            ApplyFilter();
            IsEmpty = _allCards.Count == 0;
            _logger.LogInformation(
                "直播分区：命中缓存 {Count} 张（{Age:F1} 分钟前）",
                _allCards.Count,
                (DateTimeOffset.Now - Services.LiveSectionCache.UpdatedAt).TotalMinutes);

            // 缓存里可能留着早就下播的房间卡片：顺手按真实开播状态核一遍.
            await PruneOfflineLiveCardsAsync(force: false);

            if (Services.LiveSectionCache.IsStale)
            {
                _ = RefreshInternalAsync();
            }

            return;
        }

        await LoadAsync();

        // 刚拉过一次网络，顺手核一遍卡片是不是都还在播.
        await PruneOfflineLiveCardsAsync(force: false);
    }

    [RelayCommand]
    private void Refresh() => _ = RefreshInternalAsync();

    private async Task RefreshInternalAsync()
    {
        // 手动刷新是用户主动点的：先按真实开播状态清掉已下播的卡片，不受冷却限制.
        await PruneOfflineLiveCardsAsync(force: true);

        // 刷新不再推倒重来：保留已经渲染好的卡片，把新开播的房间插到最前面、
        // 新回放追加到后面，界面上只多出新增的那几张.
        await LoadAsync(appendMode: true);
    }

    /// <summary>
    /// 按"这个人现在还在不在播"清掉缓存里残留的直播间卡片。
    /// </summary>
    /// <param name="force">true = 手动刷新触发，忽略冷却.</param>
    /// <returns>任务.</returns>
    private async Task PruneOfflineLiveCardsAsync(bool force)
    {
        // 限定条件 1：只有标了"直播中"的卡片才核对；回放/切片卡片不碰.
        var liveCards = _allCards
            .Where(static p => p.IsLive && !string.IsNullOrEmpty(p.AuthorId))
            .ToList();
        if (liveCards.Count == 0)
        {
            return;
        }

        // 限定条件 2：进页面自动核对有冷却，免得切来切去一直打接口.
        if (!force && DateTimeOffset.Now - _lastPruneAt < PruneCooldown)
        {
            return;
        }

        _lastPruneAt = DateTimeOffset.Now;
        try
        {
            var cookie = this.Get<IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;
            var statuses = await Services.Live.LiveFollowService.GetLiveStatusByUidsAsync(
                liveCards.Select(static p => p.AuthorId),
                cookie);

            // 限定条件 3：接口一批都没查成（网络不通）就原样保留，不能拿"查不到"当"没开播".
            if (statuses is null)
            {
                _logger.LogWarning("直播分区：开播状态接口没返回，跳过本次清理");
                return;
            }

            // 限定条件 4：只有 live_status == 1（正在直播）才算还在；0 未开播、2 轮播都清理.
            var offline = liveCards
                .Where(p => !statuses.TryGetValue(ParseAuthorId(p.AuthorId), out var status) || status != 1)
                .ToList();
            if (offline.Count == 0)
            {
                _logger.LogInformation("直播分区：核对 {Count} 个主播，没有下播残留", liveCards.Count);
                return;
            }

            foreach (var card in offline)
            {
                _allCards.Remove(card);
            }

            // 作者可能因此没有卡片了：过滤栏一起重建，选中项尽量保住.
            var keepUserId = SelectedUper is { IsTotal: false } ? SelectedUper.UserId : null;
            BuildUpers(_allCards);
            SelectedUper = keepUserId is null
                ? Upers.FirstOrDefault()
                : Upers.FirstOrDefault(p => p.UserId == keepUserId) ?? Upers.FirstOrDefault();
            if (SelectedUper is not null)
            {
                SelectedUper.IsSelected = true;
            }

            _visibleCount = Math.Max(_visibleCount, PageBatchSize);
            ApplyFilter();
            IsEmpty = _allCards.Count == 0;

            // 磁盘缓存也一起更新，否则下次进来又把下播的卡片铺回去.
            Services.LiveSectionCache.Save(_allCards);

            _logger.LogInformation(
                "直播分区：清理已下播卡片 {Removed} 张（核对 {Checked} 张，触发={Trigger}）",
                offline.Count,
                liveCards.Count,
                force ? "手动刷新" : "进入页面");
        }
        catch (Exception ex)
        {
            // 清理失败不影响页面：卡片保持原样.
            _logger.LogWarning(ex, "直播分区：清理已下播卡片失败");
        }
    }

    /// <summary>
    /// 把卡片里的作者 id 转成 uid（转不出来返回 0）.
    /// </summary>
    /// <param name="authorId">作者 id.</param>
    /// <returns>uid.</returns>
    private static long ParseAuthorId(string authorId)
        => long.TryParse(authorId, out var uid) ? uid : 0;

    private async Task LoadAsync(bool appendMode = false)
    {
        if (IsLoading)
        {
            return;
        }

        // 追加模式下不显示整页 loading，也不清空任何东西.
        IsLoading = !appendMode;
        if (!appendMode)
        {
            IsEmpty = false;
        }
        try
        {
            var cookies = this.Get<IBiliCookiesResolver>();
            var relationship = this.Get<IRelationshipService>();

            // 第一阶段：先把正在直播的房间放出来（这一步只依赖关注分组 + 开播状态接口）.
            var lives = await LiveFollowService.GetLiveRoomsAsync(cookies, relationship);
            _logger.LogInformation("直播分区：在播 {Count} 个", lives.Count);

            var knownRoomIds = _allCards
                .Where(p => p.IsLive && p.RoomId > 0)
                .Select(p => p.RoomId)
                .ToHashSet();
            var liveCards = new List<LiveSectionCardViewModel>();
            foreach (var live in lives.OrderByDescending(p => p.Online))
            {
                if (appendMode && knownRoomIds.Contains(live.RoomId))
                {
                    // 已经在列表里了，别重复插.
                    continue;
                }

                liveCards.Add(new LiveSectionCardViewModel(
                    string.IsNullOrEmpty(live.Title) ? $"{live.UserName} 的直播间" : live.Title,
                    // 封面优先用直播间封面，拿不到再退回主播头像.
                    NormalizeCover(string.IsNullOrEmpty(live.Cover) ? live.Face : live.Cover),
                    live.UserName,
                    live.UserId.ToString(),
                    "直播中",
                    true,
                    live.Online > 0 ? $"{live.Online:N0} 人气" : "正在直播",
                    ToUri(live.Face))
                {
                    RoomId = live.RoomId,
                });
            }

            if (appendMode)
            {
                // 新开播的插到最前面，旧卡片保持原位.
                _allCards.InsertRange(0, liveCards);
                AppendUpers(liveCards);
                PrependCards(liveCards);
            }
            else
            {
                _allCards.Clear();
                _allCards.AddRange(liveCards);
                BuildUpers(_allCards);
                SelectedUper ??= Upers.FirstOrDefault();
                ApplyFilter();
            }

            IsLoading = false;
            IsEmpty = _allCards.Count == 0;

            // 第二阶段：回放慢慢补进来，不挡直播列表（扫投稿要几秒）.
            var followedIds = await LiveFollowService.GetFollowedUserIdsAsync(relationship);
            _logger.LogInformation("直播分区：关注用户 {Count} 个", followedIds.Count);

            var replays = await LoadReplaysAsync(cookies, followedIds);
            _logger.LogInformation("直播分区：回放 {Count} 个", replays.Count);
            if (appendMode)
            {
                var knownBvids = _allCards
                    .Where(p => !string.IsNullOrEmpty(p.Bvid))
                    .Select(p => p.Bvid)
                    .ToHashSet();
                replays = [.. replays.Where(p => string.IsNullOrEmpty(p.Bvid) || knownBvids.Add(p.Bvid))];
            }

            _allCards.AddRange(replays);

            // 增量追加：只把新卡片塞进列表、只把新作者塞进过滤栏，
            // 整页（含已经渲染好的直播卡）不重建.
            AppendUpers(replays);
            AppendCards(replays);
            IsEmpty = _allCards.Count == 0;
            Services.LiveSectionCache.Save(_allCards);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载直播分区失败");
            IsEmpty = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 用卡片里的作者铺顶部过滤栏.
    /// </summary>
    private void BuildUpers(List<LiveSectionCardViewModel> cards)
    {
        Upers.Clear();
        Upers.Add(new LiveSectionUperViewModel(string.Empty, "全部", null, SelectUper));
        AppendUpers(cards);
    }

    /// <summary>
    /// 只把还没出现过的作者追加到过滤栏，不动已有的项（避免整条头像栏重建）.
    /// </summary>
    /// <param name="cards">新来的卡片.</param>
    private void AppendUpers(IEnumerable<LiveSectionCardViewModel> cards)
    {
        var seen = Upers.Select(p => p.UserId).ToHashSet();
        foreach (var card in cards)
        {
            if (string.IsNullOrEmpty(card.AuthorId) || !seen.Add(card.AuthorId))
            {
                continue;
            }

            Upers.Add(new LiveSectionUperViewModel(card.AuthorId, card.AuthorName, card.Avatar, SelectUper));
        }
    }

    /// <summary>
    /// 把新到的卡片追加进列表：只在"没有按 UP 过滤、类型是全部"时才直接往后加，
    /// 这样 ItemsRepeater 只会实例化新卡片，不会把整页推倒重来.
    /// </summary>
    /// <param name="cards">新卡片.</param>
    private void AppendCards(IEnumerable<LiveSectionCardViewModel> cards)
    {
        if (SelectedUper is { IsTotal: false } || FilterType != LiveFilterType.All)
        {
            // 有过滤条件时，新卡片的可见性得整体重算，只能走完整过滤.
            ApplyFilter();
            return;
        }

        foreach (var card in cards)
        {
            // 界面上只铺窗口内的那些，多出来的等用户滚到底再补.
            if (Items.Count < _visibleCount)
            {
                Items.Add(card);
            }
        }
    }

    /// <summary>
    /// 把新开播的卡片插到列表最前面：只往头部插，已有的卡片不动.
    /// </summary>
    /// <param name="cards">新卡片（按人气从高到低）.</param>
    private void PrependCards(IReadOnlyList<LiveSectionCardViewModel> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        if (SelectedUper is { IsTotal: false } || FilterType != LiveFilterType.All)
        {
            ApplyFilter();
            return;
        }

        for (var i = cards.Count - 1; i >= 0; i--)
        {
            Items.Insert(0, cards[i]);
        }

        _visibleCount = Math.Max(_visibleCount, Items.Count);
    }

    /// <summary>
    /// 选中某个 UP 主（过滤栏在控件里通过 ItemsView 选中后回调这里）.
    /// </summary>
    /// <param name="uper">选中的项.</param>
    public void SelectUper(LiveSectionUperViewModel? uper)
    {
        if (uper is null)
        {
            return;
        }

        foreach (var item in Upers)
        {
            item.IsSelected = item == uper;
        }

        SelectedUper = uper;
        _visibleCount = PageBatchSize;
        ApplyFilter();
    }

    /// <summary>
    /// 切换内容类型过滤（全部 / 直播 / 回放 / 切片）.
    /// </summary>
    /// <param name="type">类型.</param>
    public void SetFilterType(LiveFilterType type)
    {
        FilterType = type;
        _visibleCount = PageBatchSize;
        ApplyFilter();
    }

    /// <summary>
    /// 再往界面补 12 张（滚动到底时由界面调用）；没有更多就返回 <c>false</c>.
    /// </summary>
    /// <returns>是否真的补进来了新卡片.</returns>
    public bool LoadNextBatch()
    {
        var filtered = GetFilteredCards().ToList();
        if (Items.Count >= filtered.Count)
        {
            return false;
        }

        var next = filtered.Skip(Items.Count).Take(PageBatchSize).ToList();
        _visibleCount = Items.Count + next.Count;
        foreach (var card in next)
        {
            Items.Add(card);
        }

        Serilog.Log.Information("动态哨兵（直播）：补到 {Shown}/{Total}", Items.Count, filtered.Count);
        return next.Count > 0;
    }

    /// <summary>
    /// 按「选中的 UP 主 + 内容类型」过滤要展示的卡片.
    /// </summary>
    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var card in GetFilteredCards().Take(_visibleCount))
        {
            Items.Add(card);
        }
    }

    /// <summary>
    /// 按「选中的 UP 主 + 内容类型」筛出全部候选卡片（还没有按窗口截断）.
    /// </summary>
    private IEnumerable<LiveSectionCardViewModel> GetFilteredCards()
    {
        IEnumerable<LiveSectionCardViewModel> source = _allCards;
        if (SelectedUper is { IsTotal: false })
        {
            source = source.Where(p => p.AuthorId == SelectedUper.UserId);
        }

        return FilterType switch
        {
            LiveFilterType.Live => source.Where(p => p.IsLive),
            LiveFilterType.Replay => source.Where(p => p.IsReplay),
            LiveFilterType.Clip => source.Where(p => p.IsClip),
            _ => source,
        };
    }

    /// <summary>
    /// 用搜索接口找出关注 UP 主的"直播回放 / 录播"视频。
    /// 说明：这里是按关键词搜全站再按关注的 uid 过滤，不是把每个 UP 的视频列表都翻一遍
    /// （关注几百人时那样要发几百个请求）.
    /// </summary>
    private async Task<List<LiveSectionCardViewModel>> LoadReplaysAsync(
        IBiliCookiesResolver cookies,
        List<string> followedIds)
    {
        return await LoadReplaysFromMomentsAsync();
    }

    private async Task<List<LiveSectionCardViewModel>> LoadReplaysFromMomentsAsync()
    {
        var result = new List<LiveSectionCardViewModel>();
        if (_momentService is null)
        {
            return result;
        }

        try
        {
            // 直接翻"关注 UP 主的投稿动态"，从标题里筛"直播回放 / 录播"。
            // 之前用全站搜索 + 关注 uid 过滤，搜出来的都是大主播，关注的人根本不在结果里，
            // 难怪一直是 0.
            string? offset = default;
            string? baseline = default;
            var scanned = 0;
            for (var page = 0; page < 3; page++)
            {
                var view = await _momentService.GetVideoMomentsAsync(offset, baseline);
                if (view is null || view.Moments.Count == 0)
                {
                    break;
                }

                offset = view.Offset;
                baseline = view.UpdateBaseline;

                foreach (var moment in view.Moments)
                {
                    scanned++;
                    if (moment.Data is not Richasy.BiliKernel.Models.Media.VideoInformation video)
                    {
                        continue;
                    }

                    var title = video.Identifier.Title ?? string.Empty;
                    var keyword = ReplayKeywords.FirstOrDefault(p => title.Contains(p, StringComparison.OrdinalIgnoreCase));
                    if (keyword is null)
                    {
                        continue;
                    }

                    // 回放卡必须带上 BV 号：LiveSectionCardViewModel.Play() 里直播分支看 RoomId、
                    // 回放分支看 Bvid，两个都空就只会弹个「加载中」然后什么都不发生.
                    var replayCard = new LiveSectionCardViewModel(
                        title,
                        NormalizeCover(video.Identifier.Cover?.Uri?.ToString()),
                        moment.User?.Name ?? string.Empty,
                        moment.User?.Id ?? string.Empty,
                        keyword,
                        false,
                        "回放",
                        moment.User?.Avatar?.Uri)
                    {
                        Bvid = video.Identifier.Id,
                    };
                    result.Add(replayCard);
                }

                if (view.HasMoreMoments != true)
                {
                    break;
                }
            }

            _logger.LogInformation("直播分区：扫了 {Scanned} 条投稿，出现 {Hit} 条回放", scanned, result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载直播回放失败");
        }

        return result;
    }
    private static string CleanHtml(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", string.Empty);

    private static Uri? ToUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var value = url.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        value = value.Replace("http://", "https://");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string NormalizeCover(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var value = url.Trim();
        if (value.StartsWith("//"))
        {
            value = "https:" + value;
        }

        return value.Replace("http://", "https://");
    }
}
























