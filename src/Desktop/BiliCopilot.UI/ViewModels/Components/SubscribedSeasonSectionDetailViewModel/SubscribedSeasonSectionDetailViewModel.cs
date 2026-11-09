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
                if (item.AvatarUri is null && videos is { Count: > 0 })
                {
                    item.AvatarUri = videos[0].Publisher?.User?.Avatar?.Uri;
                }
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

    private void Fill(IReadOnlyList<VideoFavoriteFolder> seasons)
    {
        foreach (var season in seasons)
        {
            Items.Add(new SeasonSubscriptionItemViewModel(season));
        }

        IsEmpty = Items.Count == 0;
    }
}




