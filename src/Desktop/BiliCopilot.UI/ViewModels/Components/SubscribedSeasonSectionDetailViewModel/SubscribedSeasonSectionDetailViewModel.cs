// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using Humanizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Collections.ObjectModel;
using BiliCopilot.UI.Services;
using System.Globalization;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 订阅合集分区视图模型.
/// 一个分区里放的是「已订阅的 UP 主合集」，每一项点开才是合集内的视频.
/// </summary>
public sealed partial class SubscribedSeasonSectionDetailViewModel : ViewModelBase, IFavoriteSectionDetailViewModel
{
    private readonly IFavoriteService _service;
    private readonly ILogger<SubscribedSeasonSectionDetailViewModel> _logger;
    private IReadOnlyList<VideoFavoriteFolder> _seasons;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscribedSeasonSectionDetailViewModel"/> class.
    /// </summary>
    /// <param name="seasons">已订阅的合集.</param>
    /// <param name="service">收藏服务.</param>
    public SubscribedSeasonSectionDetailViewModel(
        IReadOnlyList<VideoFavoriteFolder> seasons,
        IFavoriteService service)
    {
        _seasons = seasons;
        _service = service;
        _logger = this.Get<ILogger<SubscribedSeasonSectionDetailViewModel>>();
    }

    /// <summary>
    /// 合集列表.
    /// </summary>
    public ObservableCollection<SeasonSubscriptionItemViewModel> Items { get; } = new();

    /// <summary>
    /// 没有内容.
    /// </summary>
    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// 正在加载.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <inheritdoc/>
    [RelayCommand]
    private async Task TryFirstLoadAsync()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Fill(_seasons);
        _ = EnrichUpdateTimesAsync();
    }

    /// <inheritdoc/>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsLoading = true;
            var (groups, _) = await _service.GetVideoFavoriteGroupsAsync();
            _seasons = groups
                .SelectMany(p => p.Folders)
                .Where(p => p.IsUgcSeason == true)
                .ToList();
            Items.Clear();
            Fill(_seasons);
            _ = EnrichUpdateTimesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新订阅合集失败");
        }
        finally
        {
            IsLoading = false;
            IsEmpty = Items.Count == 0;
        }
    }

    /// <summary>
    /// 合集更新时间要靠合集内视频推：取第一页里最新的发布时间.
    /// 一个合集一次请求，限并发，失败就保持空着，不阻塞卡片渲染.
    /// </summary>
    private async Task EnrichUpdateTimesAsync()
    {
        var snapshot = Items.ToList();
        using var gate = new SemaphoreSlim(3);
        var tasks = snapshot.Select(async item =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var (videos, _, _) = await _service.GetUgcSeasonVideosAsync(item.Data, 0).ConfigureAwait(false);

                // 头像必须是「合集创建者本人」的。
                // 之前直接拿合集内第一个视频的发布者，联合投稿时那就是合作者的头像 —— 所以显示成了别人。
                // 顺序：列表里的 upper → 用户接口按 mid 查 → 收藏夹详情里的 upper.face →
                // 合集内与创建者同 id 的发布者 → 最后才退到第一个视频的发布者.
                await ResolveOwnerAvatarAsync(item, videos).ConfigureAwait(false);
                Services.StartupTrace.Mark("SeasonCard:resolve-avatar", $"{item.Title} avatar={item.AvatarUri?.ToString() ?? "null"}");

                var latest = videos?
                    .Where(p => p.PublishTime is not null)
                    .Select(p => p.PublishTime!.Value)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Max();
                if (latest is { } time && time > DateTimeOffset.MinValue)
                {
                    item.UpdateText = $"{time.Humanize(culture: new CultureInfo("zh-CN"))}更新";
                }
            }
            catch
            {
                // 拿不到就不显示，别把卡片搞崩.
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 解析合集创建者的头像。合集列表接口经常只给名字不给头像，
    /// 而合集内第一条视频的发布者可能是联合投稿的合作者——直接拿来用就会显示成别人的头像.
    /// </summary>
    /// <param name="item">合集卡片.</param>
    /// <param name="videos">合集内第一页视频.</param>
    /// <returns>任务.</returns>
    private async Task ResolveOwnerAvatarAsync(SeasonSubscriptionItemViewModel item, IReadOnlyList<VideoInformation>? videos)
    {
        if (item.AvatarUri is not null)
        {
            return;
        }

        var ownerId = item.Data.User?.Id;
        var ownerName = item.Data.User?.Name;

        // 1. 用 mid 查用户信息，这才是本人头像.
        if (!string.IsNullOrEmpty(ownerId))
        {
            try
            {
                var userService = this.Get<IUserService>();
                var profile = await userService.GetUserInformationAsync(ownerId).ConfigureAwait(false);
                if (profile?.Profile?.User?.Avatar?.Uri is { } avatarUri)
                {
                    item.AvatarUri = avatarUri;
                    return;
                }
            }
            catch
            {
            }
        }

        // 2. 收藏夹详情里的 upper.face.
        try
        {
            var detail = await _service.GetVideoFavoriteFolderDetailAsync(item.Data, 0).ConfigureAwait(false);
            if (detail.Detail?.Folder?.User?.Avatar?.Uri is { } detailAvatar)
            {
                item.AvatarUri = detailAvatar;
                return;
            }

            ownerId ??= detail.Detail?.Folder?.User?.Id;
            ownerName ??= detail.Detail?.Folder?.User?.Name;
        }
        catch
        {
        }

        if (videos is not { Count: > 0 })
        {
            return;
        }

        // 3. 合集内找同一个人（联合投稿时有用）：先按 id，再按名字.
        var publisher = !string.IsNullOrEmpty(ownerId)
            ? videos.Select(p => p.Publisher?.User).FirstOrDefault(u => u is not null && u.Id == ownerId)
            : null;
        publisher ??= !string.IsNullOrEmpty(ownerName)
            ? videos.Select(p => p.Publisher?.User).FirstOrDefault(u => u is not null && u.Name == ownerName)
            : null;

        if (publisher?.Avatar?.Uri is { } matchedAvatar)
        {
            item.AvatarUri = matchedAvatar;
            return;
        }

        // 4. 都失败才退到第一个视频的发布者.
        item.AvatarUri = videos[0].Publisher?.User?.Avatar?.Uri;
    }

    private void Fill(IReadOnlyList<VideoFavoriteFolder> seasons)
    {
        foreach (var season in seasons)
        {
            Items.Add(new SeasonSubscriptionItemViewModel(season));
        }

        IsEmpty = Items.Count == 0;
    }
}










