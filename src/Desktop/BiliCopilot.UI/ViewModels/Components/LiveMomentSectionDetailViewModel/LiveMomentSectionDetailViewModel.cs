// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
            var followedIds = await LiveFollowService.GetFollowedUserIdsAsync(relationship);
            _logger.LogInformation("直播分区：关注用户 {Count} 个", followedIds.Count);

            var lives = await LiveFollowService.GetLiveRoomsAsync(cookies, relationship);
            _logger.LogInformation("直播分区：在播 {Count} 个", lives.Count);

            var replays = await LoadReplaysAsync(cookies, followedIds);
            _logger.LogInformation("直播分区：回放 {Count} 个", replays.Count);

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

            _allCards.AddRange(replays);

            BuildUpers(_allCards);
            SelectUper(Upers.FirstOrDefault());
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
        Items.Clear();
        var source = uper.IsTotal
            ? _allCards
            : _allCards.Where(p => p.AuthorId == uper.UserId);
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









