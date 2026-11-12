// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频播放器合集视图模型.
/// </summary>
public sealed partial class VideoPlayerSeasonSectionDetailViewModel : ViewModelBase, IPlayerSectionDetailViewModel
{
    /// <summary>
    /// 当前播放页面正在播放的视频Id.
    /// </summary>
    private readonly string _videoId;

    /// <summary>每次往界面补多少张卡片.</summary>
    private const int BatchSize = 6;

    [ObservableProperty]
    private List<VideoSeason> _seasons;

    [ObservableProperty]
    private List<VideoItemViewModel> _items;

    [ObservableProperty]
    private VideoSeason _selectedSeason;

    /// <summary>
    /// 当前已经铺到界面上的卡片（分批加载，避免一次性建几十张封面卡）.
    /// </summary>
    [ObservableProperty]
    private List<VideoItemViewModel> _visibleItems = [];

    /// <summary>
    /// 是否还有没铺完的卡片.
    /// </summary>
    [ObservableProperty]
    private bool _hasMoreItems;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPlayerSeasonSectionDetailViewModel"/> class.
    /// </summary>
    public VideoPlayerSeasonSectionDetailViewModel(
        VideoConnectorViewModel page,
        IList<VideoSeason> seasons,
        string videoId)
    {
        Page = page;
        Seasons = [.. seasons];
        _videoId = videoId;
        var selectedSeason = Seasons.Find(p => p.Videos.Any(q => q.Identifier.Id == _videoId));
        ChangeSeason(selectedSeason);
    }

    /// <inheritdoc/>
    public string Title => ResourceToolkit.GetLocalizedString(StringNames.UgcSeason);

    /// <summary>
    /// 页面视图模型.
    /// </summary>
    public VideoConnectorViewModel Page { get; }

    [RelayCommand]
    private static Task TryFirstLoadAsync()
        => Task.CompletedTask;

    [RelayCommand]
    private void ChangeSeason(VideoSeason season)
    {
        if (season is null || season == SelectedSeason)
        {
            return;
        }

        SelectedSeason = season;
        Items = [.. season.Videos.Select(p => new VideoItemViewModel(p, Models.Constants.VideoCardStyle.PlayerSeason, playAction: Play))];
        foreach (var item in Items)
        {
            item.IsSelected = item.Data.Identifier.Id == _videoId;
        }

        // 先只铺第一批，剩下的等用户滚到底部再补.
        VisibleItems = [.. Items.Take(BatchSize)];
        HasMoreItems = Items.Count > BatchSize;
    }

    /// <summary>
    /// 往上再铺 6 张（由界面滚动到底部时调用；已经铺完就什么也不做）.
    /// </summary>
    public void LoadNextBatch()
    {
        var total = Items?.Count ?? 0;
        var shown = VisibleItems?.Count ?? 0;
        if (shown >= total)
        {
            HasMoreItems = false;
            return;
        }

        shown = Math.Min(total, shown + BatchSize);
        VisibleItems = [.. Items.Take(shown)];
        HasMoreItems = shown < total;
        Serilog.Log.Information("合集滚动加载：{Shown}/{Total}", shown, total);
    }

    private void Play(VideoItemViewModel vm)
    {
        if (vm.Data.Identifier.Id == _videoId)
        {
            return;
        }

        var snapshot = new MediaSnapshot(vm.Data, Page.IsPrivatePlay);
        Page.Parent.InitializeCommand.Execute(snapshot);
    }
}


