// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using System.Text.Json;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型 - 直播.
/// 弹幕与互动消息直连广播服务器解析，画质线路、榜单、大航海、礼物背包走直播接口.
/// </summary>
public sealed partial class PlayerViewModel
{
    private LiveDanmakuClient? _liveClient;
    private LiveRoomDetail? _liveDetail;
    private CancellationTokenSource? _liveCancellationTokenSource;
    private DispatcherQueueTimer? _liveUptimeTimer;
    private DateTimeOffset _liveUptimeBase = DateTimeOffset.MinValue;
    private readonly HashSet<string> _loggedRaw = new();

    /// <summary>
    /// 互动消息（弹幕 / 礼物 / 醒目留言 / 上舰 / 进场）.
    /// </summary>
    public ObservableCollection<LiveInteractionViewModel> LiveInteractions { get; } = new();

    /// <summary>
    /// 礼物榜.
    /// </summary>
    public ObservableCollection<LiveRankItemViewModel> LiveRanks { get; } = new();

    /// <summary>
    /// 大航海列表.
    /// </summary>
    public ObservableCollection<LiveGuardItemViewModel> LiveGuards { get; } = new();

    /// <summary>
    /// 可选画质与线路.
    /// </summary>
    public ObservableCollection<LiveStreamOptionViewModel> LiveStreamOptions { get; } = new();

    /// <summary>
    /// 礼物背包（免费礼物）.
    /// </summary>
    public ObservableCollection<LiveGiftBagItem> LiveGiftBag { get; } = new();

    /// <summary>
    /// 当前是否在播放直播.
    /// </summary>
    [ObservableProperty]
    private bool _isLive;

    /// <summary>
    /// 直播标题.
    /// </summary>
    [ObservableProperty]
    private string _liveTitle = string.Empty;

    /// <summary>
    /// 直播 UP 主.
    /// </summary>
    [ObservableProperty]
    private UserProfile? _liveUser;

    /// <summary>
    /// 在线人气.
    /// </summary>
    [ObservableProperty]
    private string _liveOnlineText = "0";

    /// <summary>
    /// 粉丝数.
    /// </summary>
    [ObservableProperty]
    private string _liveFansText = "0";

    /// <summary>
    /// 直播已播时长.
    /// </summary>
    [ObservableProperty]
    private string _liveUptimeText = "00:00:00";

    /// <summary>
    /// 直播弹幕输入内容.
    /// </summary>
    [ObservableProperty]
    private string _liveDanmakuInput = string.Empty;

    /// <summary>
    /// 是否已关注主播.
    /// </summary>
    [ObservableProperty]
    private bool _isLiveUserFollowed;

    /// <summary>
    /// 直播状态提示.
    /// </summary>
    [ObservableProperty]
    private string _liveStatusText = string.Empty;

    /// <summary>
    /// 是否显示直播弹幕浮层.
    /// </summary>
    [ObservableProperty]
    private bool _isLiveDanmakuVisible = true;

    /// <summary>
    /// 当前画质文本.
    /// </summary>
    [ObservableProperty]
    private string _liveQualityText = "原画";

    /// <summary>
    /// 当前线路文本.
    /// </summary>
    [ObservableProperty]
    private string _liveLineText = "线路1";

    /// <summary>
    /// 当前选中的直播流.
    /// </summary>
    [ObservableProperty]
    private LiveStreamOptionViewModel? _selectedLiveStream;

    /// <summary>
    /// 金瓜子余额.
    /// </summary>
    [ObservableProperty]
    private string _liveGoldText = "0";

    /// <summary>
    /// 银瓜子余额.
    /// </summary>
    [ObservableProperty]
    private string _liveSilverText = "0";

    private string LiveRoomId => _snapshot?.Live?.Identifier.Id ?? string.Empty;

    /// <summary>
    /// 直播面板当前分区（0 互动 / 1 榜单 / 2 大航海）.
    /// </summary>
    [ObservableProperty]
    private int _livePanelTab;

    /// <summary>
    /// 主播头像地址（统一成绝对地址，取不到为空串）.
    /// </summary>
    public string LiveUserAvatarUrl
    {
        get
        {
            var url = _liveDetail?.AnchorAvatar;
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var value = url.Trim();
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                value = "https:" + value;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : string.Empty;
        }
    }

    /// <summary>
    /// 主播头像.
    /// </summary>
    public Microsoft.UI.Xaml.Media.ImageSource? LiveUserAvatar
        => string.IsNullOrEmpty(LiveUserAvatarUrl)
            ? null
            : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(LiveUserAvatarUrl));

    /// <summary>
    /// 关注按钮文本.
    /// </summary>
    public string FollowButtonText => IsLiveUserFollowed ? "已关注" : "关注";

    /// <summary>
    /// 画质与线路文本.
    /// </summary>
    public string LiveQualityAndLineText => $"{LiveQualityText} · {LiveLineText}";

    /// <summary>
    /// 钱包余额文本.
    /// </summary>
    public string LiveWalletText => $"金瓜子 {LiveGoldText} · 银瓜子 {LiveSilverText}";

    [RelayCommand]
    private void SwitchLiveTab(int index)
    {
        if (index is >= 0 and <= 2)
        {
            LivePanelTab = index;
        }
    }

    partial void OnIsLiveUserFollowedChanged(bool value) => OnPropertyChanged(nameof(FollowButtonText));

    partial void OnLiveQualityTextChanged(string value) => OnPropertyChanged(nameof(LiveQualityAndLineText));

    partial void OnLiveLineTextChanged(string value) => OnPropertyChanged(nameof(LiveQualityAndLineText));

    partial void OnLiveGoldTextChanged(string value) => OnPropertyChanged(nameof(LiveWalletText));

    partial void OnLiveSilverTextChanged(string value) => OnPropertyChanged(nameof(LiveWalletText));

    private async Task ConnectLiveAsync(CancellationToken cancellationToken)
    {
        if (_snapshot?.Live is not null)
        {
            LiveTitle = _snapshot.Live.Identifier.Title;
            LiveUser = _snapshot.Live.User;
            IsLiveUserFollowed = _snapshot.Live.Relation is UserRelationStatus.Following or UserRelationStatus.Friends or UserRelationStatus.SpeciallyFollowed;
        }

        if (string.IsNullOrEmpty(LiveRoomId))
        {
            return;
        }

        _liveCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _liveCancellationTokenSource.Token;

        await LoadLiveRoomInfoAsync(token);
        await LoadLiveListsAsync(token);
        await ConnectLiveDanmakuAsync(token);
        StartUptimeTimer();
    }

    private async Task LoadLiveRoomInfoAsync(CancellationToken token)
    {
        var cookie = GetCookieString();
        var detail = await LiveRoomApi.GetRoomDetailAsync(LiveRoomId, cookie, logger);
        if (detail is null)
        {
            LiveStatusText = "直播间信息获取失败";
            return;
        }

        _liveDetail = detail;
        if (!string.IsNullOrEmpty(detail.Title))
        {
            LiveTitle = detail.Title;
        }

        LiveOnlineText = FormatCount(detail.Online);
        if (detail.FansCount > 0)
        {
            LiveFansText = detail.FansCount.ToString("N0");
        }
        else
        {
            // 直播间详情偶尔不带粉丝数（接口失败时就是 0），单独问一次关注关系.
            _ = FillFansCountAsync(detail.AnchorUid.ToString(), logger);
        }
        if (detail.StartTime > 0)
        {
            _liveUptimeBase = DateTimeOffset.FromUnixTimeSeconds(detail.StartTime);
        }

        UpdateUptime();

        // 画质与线路.
        var options = await LiveRoomApi.GetStreamOptionsAsync(LiveRoomId, cookie, logger);
        LiveStreamOptions.Clear();
        foreach (var option in options)
        {
            LiveStreamOptions.Add(new LiveStreamOptionViewModel(option));
        }

        logger.LogInformation("直播间 {Room} 在线 {Online}，可选流 {Count} 条", LiveRoomId, LiveOnlineText, LiveStreamOptions.Count);

        var current = LiveStreamOptions.FirstOrDefault();
        if (current is not null)
        {
            SelectedLiveStream = current;
            LiveQualityText = current.QualityText;
            LiveLineText = current.LineText;
        }
    }

    private async Task LoadLiveListsAsync(CancellationToken token)
    {
        var cookie = GetCookieString();
        var anchorUid = _liveDetail?.AnchorUid ?? 0;
        if (anchorUid == 0)
        {
            anchorUid = await LiveRoomApi.GetAnchorUidAsync(LiveRoomId, cookie, logger);
        }
        try
        {
            var ranks = await LiveRoomApi.GetGoldRankAsync(LiveRoomId, anchorUid, cookie, logger);
            LiveRanks.Clear();
            foreach (var item in ranks)
            {
                LiveRanks.Add(new LiveRankItemViewModel(item));
            }

            var guards = await LiveRoomApi.GetGuardListAsync(LiveRoomId, anchorUid, cookie, logger);
            LiveGuards.Clear();
            foreach (var item in guards)
            {
                LiveGuards.Add(new LiveGuardItemViewModel(item));
            }

            var giftBag = await LiveRoomApi.GetGiftBagAsync(LiveRoomId, cookie, logger);
            LiveGiftBag.Clear();
            foreach (var item in giftBag)
            {
                LiveGiftBag.Add(item);
            }

            var (gold, silver) = await LiveRoomApi.GetWalletAsync(cookie, logger);
            LiveGoldText = gold.ToString("N0");
            LiveSilverText = silver.ToString("N0");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "加载直播榜单失败");
        }

        await Task.CompletedTask;
    }

    private async Task ConnectLiveDanmakuAsync(CancellationToken token)
    {
        try
        {
            _liveClient = new LiveDanmakuClient();
            _liveClient.MessageReceived += OnLiveMessageReceived;
            _liveClient.StateChanged += OnLiveStateChanged;
            await _liveClient.ConnectAsync(LiveRoomId, GetCookieString(), GetMyUid(), logger);
        }
        catch (Exception ex)
        {
            LiveStatusText = "弹幕连接失败";
            logger.LogWarning(ex, "直播弹幕连接失败.");
        }
    }

    private void OnLiveMessageReceived(object? sender, (string Command, JsonElement Data) e)
    {
        var cmd = e.Command.Split(':')[0];

        if (_loggedRaw.Add(cmd))
        {
            var raw = e.Data.GetRawText();
            logger.LogInformation("弹幕原始样本 {Cmd}：{Raw}", cmd, raw.Length > 700 ? raw[..700] : raw);
        }

        switch (cmd)
        {
            case "WATCHED_CHANGE":
                // 「x 人看过」这种累计观看数，跟直播间人气不是一个口径，留着不用.
                break;
            case "ONLINE_RANK_COUNT":
                // 这是榜单里的人数（高能榜/在线榜），不是直播间人气，不去覆盖 LiveOnlineText，否则数字来回跳.
                break;
            case "ROOM_REAL_TIME_MESSAGE_UPDATE":
                var fans = ReadProp(e.Data, "data", "fans");
                if (!string.IsNullOrEmpty(fans))
                {
                    queue.TryEnqueue(() => LiveFansText = long.TryParse(fans, out var value) ? $"{value:N0}" : fans);
                }

                break;
            case "LIVE":
                queue.TryEnqueue(() => LiveStatusText = "直播中");
                break;
            case "PREPARING":
                queue.TryEnqueue(() => LiveStatusText = "未开播");
                break;
            case "ROOM_CHANGE":
                var title = ReadProp(e.Data, "data", "title");
                if (!string.IsNullOrEmpty(title))
                {
                    queue.TryEnqueue(() => LiveTitle = title);
                }

                break;
            default:
                var item = LiveMessageParser.Parse(e.Command, e.Data);
                if (item is not null)
                {
                    queue.TryEnqueue(() => AppendLiveInteraction(item));
                }

                break;
        }
    }

    private void OnLiveStateChanged(object? sender, string state)
        => queue.TryEnqueue(() => LiveStatusText = state);

    private void AppendLiveInteraction(LiveInteractionItem item)
    {
        LiveInteractions.Add(new LiveInteractionViewModel(item));
        while (LiveInteractions.Count > 200)
        {
            LiveInteractions.RemoveAt(0);
        }
    }

    private void StartUptimeTimer()
    {
        _liveUptimeTimer ??= queue.CreateTimer();
        _liveUptimeTimer.Interval = TimeSpan.FromSeconds(1);
        _liveUptimeTimer.Tick -= OnLiveUptimeTick;
        _liveUptimeTimer.Tick += OnLiveUptimeTick;
        _liveUptimeTimer.Start();
    }

    private void OnLiveUptimeTick(DispatcherQueueTimer sender, object args) => UpdateUptime();

    private void UpdateUptime()
    {
        UpdateLiveLatency();
        if (_liveUptimeBase == DateTimeOffset.MinValue)
        {
            LiveUptimeText = "00:00:00";
            return;
        }

        var span = DateTimeOffset.Now - _liveUptimeBase;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        LiveUptimeText = $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    private string GetCookieString()
        => this.Get<IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;

    private long GetMyUid()
        => this.Get<AccountViewModel>().MyProfile?.User?.Id is var id && long.TryParse(id, out var value) ? value : 0;

    private static string FormatCount(int value)
    {
        if (value < 10000)
        {
            return value.ToString();
        }

        // 55.0 万这种多余的小数点去掉，55 万 / 4.3 万.
        var wan = value / 10000d;
        return wan % 1 == 0 ? $"{(int)wan} 万" : $"{wan:0.#} 万";
    }

    private static string ReadProp(JsonElement root, string parent, string name)
    {
        if (!root.TryGetProperty(parent, out var node) || node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    [RelayCommand]
    private async Task SendLiveDanmakuAsync()
    {
        var text = LiveDanmakuInput?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var sent = _liveClient is not null && await _liveClient.SendDanmakuAsync(LiveRoomId, text);
            if (!sent)
            {
                var cookie = GetCookieString();
                var csrf = ExtractCookie(cookie, "bili_jct");
                sent = await LiveRoomApi.SendDanmakuAsync(LiveRoomId, text, cookie, csrf, logger);
            }

            LiveDanmakuInput = sent ? string.Empty : LiveDanmakuInput;
            LiveStatusText = sent ? "弹幕已发送" : "弹幕发送失败";
            logger.LogInformation("直播弹幕发送：{Text} 结果={Sent}", text, sent);
        }
        catch (Exception ex)
        {
            LiveStatusText = "弹幕发送失败";
            logger.LogWarning(ex, "发送直播弹幕失败.");
        }
    }

    [RelayCommand]
    private void SwitchLiveDanmaku() => IsLiveDanmakuVisible = !IsLiveDanmakuVisible;

    [RelayCommand]
    private async Task RefreshLiveInfoAsync()
    {
        var token = _liveCancellationTokenSource?.Token ?? CancellationToken.None;
        await LoadLiveRoomInfoAsync(token);
        await LoadLiveListsAsync(token);
    }

    [RelayCommand]
    private async Task SwitchLiveStreamAsync(LiveStreamOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }

        SelectedLiveStream = option;
        LiveQualityText = option.QualityText;
        LiveLineText = option.LineText;
        if (_sourceResolver is Resolvers.LiveMediaSourceResolver liveResolver)
        {
            liveResolver.UseUrl(option.Url);
            await Player.ReplayAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleLiveFollowAsync()
    {
        if (LiveUser is null)
        {
            return;
        }

        try
        {
            var service = this.Get<IRelationshipService>();
            if (IsLiveUserFollowed)
            {
                await service.UnfollowUserAsync(LiveUser.Id, default);
            }
            else
            {
                await service.FollowUserAsync(LiveUser.Id, default);
            }

            IsLiveUserFollowed = !IsLiveUserFollowed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "切换直播关注状态失败.");
        }
    }

    private static string ExtractCookie(string cookie, string name)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0 && part[..index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(index + 1)..];
            }
        }

        return string.Empty;
    }

    private void StopLive()
    {
        try
        {
            _liveCancellationTokenSource?.Cancel();
            if (_liveClient is not null)
            {
                _liveClient.MessageReceived -= OnLiveMessageReceived;
                _liveClient.StateChanged -= OnLiveStateChanged;
                _ = _liveClient.DisposeAsync();
            }

            _liveUptimeTimer?.Stop();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "关闭直播连接时出错.");
        }
        finally
        {
            _liveCancellationTokenSource?.Dispose();
            _liveClient = null;
            _liveCancellationTokenSource = null;
            LiveInteractions.Clear();
            LiveRanks.Clear();
            LiveGuards.Clear();
            LiveStreamOptions.Clear();
            LiveGiftBag.Clear();
        }
    }
    /// <summary>
    /// 直播开播前清掉着色器与超分：这些滤镜压在直播流上会让视频轨起不来，只剩声音.
    /// </summary>
    /// <returns>任务.</returns>
    private async Task ClearEnhancementsForLiveAsync()
    {
        if (Client is null)
        {
            return;
        }

        try
        {
            await Client.SetShadersAsync(Array.Empty<string>());
            Anime4KMode = Models.Constants.Anime4KMode.None;
            ArtCNNMode = Models.Constants.ArtCNNMode.None;
            Nnedi3Mode = Models.Constants.Nnedi3Mode.None;
            RavuMode = Models.Constants.RavuMode.None;
            VsrScale = Models.Constants.VsrScale.None;
            logger.LogInformation("直播已清空着色器与超分，避免视频轨被滤镜挡掉");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清空直播着色器失败");
        }
    }
    /// <summary>
    /// 详情里没带粉丝数时，用关注关系接口补一次.
    /// </summary>
    /// <param name="uid">主播 uid.</param>
    /// <param name="logger">日志.</param>
    /// <returns>任务.</returns>
    private async Task FillFansCountAsync(string? uid, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(uid))
        {
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/relation/stat?vmid={uid}");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com");
            var cookie = GetCookieString();
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("follower", out var follower)
                && follower.TryGetInt64(out var value)
                && value > 0)
            {
                LiveFansText = value.ToString("N0");
                logger.LogInformation("直播粉丝数已补充：{Fans}", LiveFansText);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "补充直播粉丝数失败");
        }
    }
}
