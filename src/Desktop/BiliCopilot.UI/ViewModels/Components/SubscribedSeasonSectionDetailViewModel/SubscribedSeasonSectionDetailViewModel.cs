// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Collections.ObjectModel;

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

    private void Fill(IReadOnlyList<VideoFavoriteFolder> seasons)
    {
        foreach (var season in seasons)
        {
            Items.Add(new SeasonSubscriptionItemViewModel(season));
        }

        IsEmpty = Items.Count == 0;
    }
}

