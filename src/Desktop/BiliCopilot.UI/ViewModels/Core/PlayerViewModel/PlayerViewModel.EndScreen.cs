// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using Richasy.WinUIKernel.Share.Base;
using Windows.ApplicationModel.DataTransfer;
using BiliCopilot.UI.ViewModels.Components;
using Richasy.BiliKernel.Bili.Media;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放结束界面.
/// 播完以后底部控制栏会收起，换成一块结束页：UP 信息、重播、互动按钮、以及一排相关推荐视频.
/// </summary>
public sealed partial class PlayerViewModel
{
    /// <summary>
    /// 是否处于"播放已结束"状态.
    /// </summary>
    [ObservableProperty]
    private bool _isPlaybackEnded;

    /// <summary>
    /// 结束页底部的相关推荐.
    /// </summary>
    public ObservableCollection<VideoItemViewModel> EndRecommendations { get; } = [];

    /// <summary>
    /// 结束页的 UP 名.
    /// </summary>
    public string EndUpName => EndConnector?.UpName ?? string.Empty;

    /// <summary>
    /// 结束页的 UP 头像.
    /// </summary>
    public Uri? EndUpAvatar => EndConnector?.UpAvatar;

    /// <summary>
    /// 是否已关注 UP.
    /// </summary>
    public bool EndIsFollow => EndConnector?.IsFollow ?? false;

    /// <summary>
    /// 是否已点赞.
    /// </summary>
    public bool EndIsLiked => EndConnector?.IsLiked ?? false;

    /// <summary>
    /// 是否已投币.
    /// </summary>
    public bool EndIsCoined => EndConnector?.IsCoined ?? false;

    /// <summary>
    /// 是否已收藏.
    /// </summary>
    public bool EndIsFavorited => EndConnector?.IsFavorited ?? false;

    /// <summary>
    /// 点赞数文字.
    /// </summary>
    public string EndLikeText => FormatEndCount(EndConnector?.LikeCount ?? 0);

    /// <summary>
    /// 投币数文字.
    /// </summary>
    public string EndCoinText => FormatEndCount(EndConnector?.CoinCount ?? 0);

    /// <summary>
    /// 收藏数文字.
    /// </summary>
    public string EndFavoriteText => FormatEndCount(EndConnector?.FavoriteCount ?? 0);

    /// <summary>
    /// 分享：直接把视频链接复制到剪贴板，并在播放窗口弹一条提示.
    /// 原来走的是系统分享面板（ShareVideoUrlCommand），在播放窗口里既多一步、提示也不可见.
    /// </summary>
    [RelayCommand]
    private void ShareFromEnd()
    {
        var connector = EndConnector;
        if (connector is null)
        {
            return;
        }

        var url = !string.IsNullOrEmpty(connector.BvId)
            ? $"https://www.bilibili.com/video/{connector.BvId}"
            : $"https://www.bilibili.com/video/av{connector.AvId}";

        var package = new DataPackage();
        package.SetText(url);
        package.SetWebLink(new Uri(url));
        Clipboard.SetContent(package);

        Window?.ShowTip("已经复制视频链接，请自行粘贴", InfoType.Success);
    }

    /// <summary>
    /// 关注命令.
    /// </summary>
    public IAsyncRelayCommand? EndFollowCommand => EndConnector?.ToggleFollowCommand;

    /// <summary>
    /// 点赞命令.
    /// </summary>
    public IAsyncRelayCommand? EndLikeCommand => EndConnector?.ToggleLikeCommand;

    /// <summary>
    /// 投币（结束页固定投 1 个）：CoinCommand 是 RelayCommand&lt;int&gt;，
    /// XAML 里把 CommandParameter 写成字符串会在 CanExecute 阶段直接抛参数类型异常，
    /// 所以这里包一层不带参数的命令.
    /// </summary>
    [RelayCommand]
    private void CoinFromEnd() => EndConnector?.CoinCommand.Execute(1);

    /// <summary>
    /// 收藏命令.
    /// </summary>
    public IAsyncRelayCommand? EndFavoriteCommand => EndConnector?.FavoriteCommand;

    /// <summary>
    /// 分享命令.
    /// </summary>
    public IRelayCommand? EndShareCommand => EndConnector?.ShareVideoUrlCommand;

    /// <summary>
    /// 投币命令（CoinButton 会用参数 1/2 调用）.
    /// </summary>
    public IRelayCommand<int>? EndCoinCommandRaw => EndConnector?.CoinCommand;

    /// <summary>
    /// 一键三连命令.
    /// </summary>
    public IAsyncRelayCommand? EndTripleCommand => EndConnector?.TripleCommand;

    /// <summary>
    /// 收藏夹列表（FavoriteButton 用）.
    /// </summary>
    public object? EndFavoriteFolders => EndConnector?.FavoriteFolders;

    /// <summary>
    /// 初始化收藏夹命令（FavoriteButton 用）.
    /// </summary>
    public IAsyncRelayCommand? EndInitFavoritesCommand => EndConnector?.InitializeFavoritesCommand;

    /// <summary>
    /// 投币时是否同时点赞.
    /// </summary>
    public bool EndCoinAlsoLike => EndConnector?.IsCoinAlsoLike ?? false;

    /// <summary>
    /// 打开 UP 主空间.
    /// </summary>
    public IRelayCommand? EndOpenUserSpaceCommand => EndConnector?.OpenUserSpaceCommand;

    /// <summary>
    /// 重播：整条链路重新加载后从头播（沿用修复"播完后点播放没反应"的那条路径）.
    /// </summary>
    [RelayCommand]
    private async Task ReplayFromEndAsync() => await ReloadAtAsync(0);

    /// <summary>
    /// 结束页用到的视频连接器（互动按钮直接绑它，保证收藏夹等异步数据能实时更新）.
    /// </summary>
    public VideoConnectorViewModel? EndConnector => Connector as VideoConnectorViewModel;

    /// <summary>
    /// 标记进入结束状态，并准备推荐数据.
    /// </summary>
    internal void MarkPlaybackEnded()
    {
        if (IsPlaybackEnded)
        {
            return;
        }

        IsPlaybackEnded = true;
        _ = LoadEndRecommendationsAsync();
        RaiseEndScreenProperties();
    }

    /// <summary>
    /// 退出结束状态（重新播放、跳转、切换视频）.
    /// </summary>
    internal void ClearPlaybackEnded()
    {
        if (IsPlaybackEnded)
        {
            IsPlaybackEnded = false;
        }
    }

    /// <summary>
    /// 换了视频或重载时把结束状态整个复位.
    /// </summary>
    internal void ResetEndScreen()
    {
        ClearPlaybackEnded();
        EndRecommendations.Clear();
    }

    /// <summary>
    /// 结束页上的信息跟着连接器一起刷新（关注/点赞/投币/收藏状态会变）.
    /// </summary>
    internal void RaiseEndScreenProperties()
    {
        OnPropertyChanged(nameof(EndUpName));
        OnPropertyChanged(nameof(EndUpAvatar));
        OnPropertyChanged(nameof(EndIsFollow));
        OnPropertyChanged(nameof(EndIsLiked));
        OnPropertyChanged(nameof(EndIsCoined));
        OnPropertyChanged(nameof(EndIsFavorited));
        OnPropertyChanged(nameof(EndLikeText));
        OnPropertyChanged(nameof(EndCoinText));
        OnPropertyChanged(nameof(EndFavoriteText));
        OnPropertyChanged(nameof(EndFollowCommand));
        OnPropertyChanged(nameof(EndLikeCommand));

        OnPropertyChanged(nameof(EndFavoriteCommand));
        OnPropertyChanged(nameof(EndShareCommand));
        OnPropertyChanged(nameof(EndOpenUserSpaceCommand));
        OnPropertyChanged(nameof(EndCoinCommandRaw));
        OnPropertyChanged(nameof(EndTripleCommand));
        OnPropertyChanged(nameof(EndFavoriteFolders));
        OnPropertyChanged(nameof(EndInitFavoritesCommand));
        OnPropertyChanged(nameof(EndCoinAlsoLike));
    }

    /// <summary>
    /// 相关推荐：优先用播放器自带的推荐分区（原版就是这个来源），
    /// 没有就回退到 BiliKernel 的通用推荐流.
    /// </summary>
    private async Task LoadEndRecommendationsAsync()
    {
        if (EndRecommendations.Count > 0)
        {
            return;
        }

        var section = EndConnector?.Sections?
            .OfType<VideoPlayerRecommendSectionDetailViewModel>()
            .FirstOrDefault();
        if (section?.Items is { Count: > 0 } items)
        {
            foreach (var item in items.Take(10))
            {
                EndRecommendations.Add(item);
            }

            return;
        }

        try
        {
            var service = this.Get<IVideoDiscoveryService>();
            var (videos, _) = await service.GetRecommendVideoListAsync(0);
            if (videos is null)
            {
                return;
            }

            foreach (var video in videos.Take(10))
            {
                EndRecommendations.Add(new VideoItemViewModel(video, VideoCardStyle.PlayerRecommend, playAction: PlayFromEnd));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载结束页推荐视频失败");
        }
    }

    private void PlayFromEnd(VideoItemViewModel vm)
    {
        var connector = EndConnector;
        if (connector is null)
        {
            return;
        }

        InitializeCommand.Execute(new UI.Models.MediaSnapshot(vm.Data, connector.IsPrivatePlay));
    }

    private static string FormatEndCount(double value)
        => value >= 10000
            ? $"{value / 10000:F1}万"
            : Math.Round(value).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
}









