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

    private static readonly string[] ReplayKeywords = ["直播回放", "录播"];

    private readonly ILogger<LiveMomentSectionDetailViewModel> _logger;
    private readonly List<LiveSectionCardViewModel> _allCards = [];
    private readonly Dictionary<string, string> _followedNames = [];
    private readonly Dictionary<string, Uri?> _followedAvatars = [];

    private readonly IMomentDiscoveryService? _momentService;

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
    /// 用户指定的切片 UP 主 id.
    /// </summary>
    public HashSet<string> ClipCreatorIds { get; private set; } = [];

    /// <summary>
    /// 切片 UP 主 id → 名字（设置里回显用）.
    /// </summary>
    public Dictionary<string, string> ClipCreatorNames { get; private set; } = [];

    /// <summary>
    /// 内容类型过滤：全部 / 直播 / 回放 / 切片.
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

        await LoadAsync();
    }

    [RelayCommand]
    private void Refresh() => _ = RefreshInternalAsync();

    private async Task RefreshInternalAsync()
    {
        _allCards.Clear();
        Upers.Clear();
        Items.Clear();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        IsEmpty = false;
        try
        {
            var cookies = this.Get<IBiliCookiesResolver>();
            var relationship = this.Get<IRelationshipService>();

            // 第一阶段：先把正在直播的房间放出来（这一步只依赖关注分组 + 开播状态接口）.
            var lives = await LiveFollowService.GetLiveRoomsAsync(cookies, relationship);
            _logger.LogInformation("直播分区：在播 {Count} 个", lives.Count);

            _allCards.Clear();
            foreach (var live in lives.OrderByDescending(p => p.Online))
            {
                _allCards.Add(new LiveSectionCardViewModel(
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

            ClipCreatorIds = ClipCreatorStore.Load();
            BuildUpers(_allCards);
            SelectedUper ??= Upers.FirstOrDefault();
            ApplyFilter();
            IsLoading = false;
            IsEmpty = _allCards.Count == 0;

            // 第二阶段：回放 / 切片慢慢补进来，不挡直播列表（扫投稿要几秒）.
            var followedIds = await LiveFollowService.GetFollowedUserIdsAsync(relationship);
            _logger.LogInformation("直播分区：关注用户 {Count} 个", followedIds.Count);
            RefreshFollowedNames(relationship);

            var replays = await LoadReplaysAsync(cookies, followedIds);
            _logger.LogInformation("直播分区：回放 {Count} 个", replays.Count);

            foreach (var card in replays)
            {
                if (ClipCreatorIds.Contains(card.AuthorId))
                {
                    card.IsClip = true;
                    card.Badge = "切片";
                }

                _allCards.Add(card);
            }

            _logger.LogInformation("直播分区：切片 UP {Count} 个，命中切片 {Hit} 条", ClipCreatorIds.Count, _allCards.Count(p => p.IsClip));

            BuildUpers(_allCards);
            SelectedUper ??= Upers.FirstOrDefault();
            ApplyFilter();
            IsEmpty = _allCards.Count == 0;
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
    /// 拉一遍关注用户的名字，切片设置里要用真实昵称而不是只有 id.
    /// </summary>
    /// <param name="relationship">关注关系服务.</param>
    private void RefreshFollowedNames(IRelationshipService relationship) => _ = LoadFollowedNamesAsync(relationship);

    private async Task LoadFollowedNamesAsync(IRelationshipService relationship)
    {
        try
        {
            var groups = await relationship.GetMyFollowUserGroupsAsync();
            if (groups is null)
            {
                return;
            }

            foreach (var group in groups)
            {
                var offset = 0;
                for (var page = 0; page < 3; page++)
                {
                    var (users, next) = await relationship.GetMyFollowUserGroupDetailAsync(group.Id, offset);
                    if (users is null || users.Count == 0)
                    {
                        break;
                    }

                    foreach (var user in users)
                    {
                        var id = user.Profile.User.Id;
                        if (!string.IsNullOrEmpty(id))
                        {
                            _followedNames[id] = user.Profile.User.Name;
                            _followedAvatars[id] = user.Profile.User.Avatar?.Uri;
                        }
                    }

                    offset = next;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉关注用户名失败（切片设置里只显示 id）");
        }
    }

    /// <summary>
    /// 可选的 UP 主列表（切片设置的选择来源），带头像.
    /// </summary>
    /// <returns>id / 名字 / 头像.</returns>
    public IReadOnlyList<(string Id, string Name, Uri? Avatar)> GetFollowedUsers()
    {
        var result = new List<(string Id, string Name, Uri? Avatar)>();
        var seen = new HashSet<string>();

        // 先用关注列表的全量用户（含头像）：这样没开播、也没投稿的 UP 也能选到.
        foreach (var pair in _followedNames)
        {
            if (!seen.Add(pair.Key))
            {
                continue;
            }

            _followedAvatars.TryGetValue(pair.Key, out var avatar);
            result.Add((pair.Key, pair.Value, avatar));
        }

        // 再补上卡片里出现过、但关注列表没覆盖到的人（比如回放的作者）.
        foreach (var card in _allCards)
        {
            if (string.IsNullOrEmpty(card.AuthorId) || !seen.Add(card.AuthorId))
            {
                continue;
            }

            var name = string.IsNullOrEmpty(card.AuthorName) ? card.AuthorId : card.AuthorName;
            result.Add((card.AuthorId, name, card.Avatar));
        }

        return result;
    }

    private void BuildUpers(List<LiveSectionCardViewModel> cards)
    {
        Upers.Clear();
        Upers.Add(new LiveSectionUperViewModel(string.Empty, "全部", null, SelectUper));

        var seen = new HashSet<string>();
        foreach (var card in cards)
        {
            if (string.IsNullOrEmpty(card.AuthorId) || !seen.Add(card.AuthorId))
            {
                continue;
            }

            _followedNames[card.AuthorId] = card.AuthorName;
            Upers.Add(new LiveSectionUperViewModel(card.AuthorId, card.AuthorName, card.Avatar, SelectUper));
        }
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
        ApplyFilter();
    }

    /// <summary>
    /// 切换内容类型过滤（全部 / 直播 / 回放 / 切片）.
    /// </summary>
    /// <param name="type">类型.</param>
    public void SetFilterType(LiveFilterType type)
    {
        FilterType = type;
        ApplyFilter();
    }

    /// <summary>
    /// 设置切片 UP 主：持久化，并把他们的投稿标成"切片"放进列表.
    /// </summary>
    /// <param name="ids">UP 主 id 集合.</param>
    public void SetClipCreators(IEnumerable<string> ids)
    {
        try
        {
            var set = ids.Where(p => !string.IsNullOrEmpty(p)).ToHashSet();
            ClipCreatorIds = set;
            ClipCreatorNames = set
                .Where(p => _followedNames.ContainsKey(p))
                .ToDictionary(p => p, p => _followedNames[p]);

            // 存文件，不写 LocalSettings —— 之前写完 LocalSettings 应用下次启动就崩.
            ClipCreatorStore.Save(set, ClipCreatorNames);

            foreach (var card in _allCards)
            {
                card.IsClip = !card.IsLive && set.Contains(card.AuthorId);
                card.Badge = card.IsClip ? "切片" : (card.IsLive ? "直播中" : "回放");
            }

            // 列表在弹窗关闭动画期间重建容易重入，推到下一轮调度.
            this.Get<Microsoft.UI.Dispatching.DispatcherQueue>().TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ApplyFilter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存切片 UP 主失败");
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        IEnumerable<LiveSectionCardViewModel> source = _allCards;
        if (SelectedUper is { IsTotal: false })
        {
            source = source.Where(p => p.AuthorId == SelectedUper.UserId);
        }

        source = FilterType switch
        {
            LiveFilterType.Live => source.Where(p => p.IsLive),
            LiveFilterType.Replay => source.Where(p => p.IsReplay),
            LiveFilterType.Clip => source.Where(p => p.IsClip),
            _ => source,
        };

        foreach (var card in source)
        {
            Items.Add(card);
        }
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

                    result.Add(new LiveSectionCardViewModel(
                        title,
                        NormalizeCover(video.Identifier.Cover?.Uri?.ToString()),
                        moment.User?.Name ?? string.Empty,
                        moment.User?.Id ?? string.Empty,
                        keyword,
                        false,
                        "回放",
                        moment.User?.Avatar?.Uri));
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
















