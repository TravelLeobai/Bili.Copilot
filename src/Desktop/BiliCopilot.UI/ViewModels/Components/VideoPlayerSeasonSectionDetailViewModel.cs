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

    /// <summary>分批加载的取消源，切换合集时取消上一轮.</summary>
    private CancellationTokenSource? _batchCts;

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

        // 上一轮分批还没铺完就取消，避免切换合集后旧批次继续追加.
        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        VisibleItems = [];
        _ = FillInBatchesAsync(_batchCts.Token);
    }

    /// <summary>
    /// 六个六个往界面上铺：先给首屏 6 张，然后每 6 张让出一次 UI 线程，
    /// 让封面解码和布局插空完成，而不是一口气建完整组.
    /// </summary>
    /// <param name="token">切换合集时用来取消上一轮.</param>
    /// <returns><see cref="Task"/>.</returns>
    private async Task FillInBatchesAsync(CancellationToken token)
    {
        const int batchSize = 6;
        var total = Items?.Count ?? 0;
        if (total == 0)
        {
            VisibleItems = [];
            HasMoreItems = false;
            return;
        }

        var shown = 0;
        try
        {
            while (shown < total)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                shown = Math.Min(total, shown + batchSize);
                VisibleItems = [.. Items.Take(shown)];
                HasMoreItems = shown < total;
                Serilog.Log.Information("合集分批加载：{Shown}/{Total}", shown, total);

                if (shown >= total)
                {
                    break;
                }

                // 让出 UI 线程：下一批等封面解码完再来.
                await Task.Delay(120, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
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


