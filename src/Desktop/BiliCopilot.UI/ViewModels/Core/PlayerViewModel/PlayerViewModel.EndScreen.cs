// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
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
    /// 打开 UP 主空间（结束页的"充电"按钮走这里）.
    /// </summary>
    public IRelayCommand? EndOpenUserSpaceCommand => EndConnector?.OpenUserSpaceCommand;

    /// <summary>
    /// 重播：整条链路重新加载后从头播（沿用修复"播完后点播放没反应"的那条路径）.
    /// </summary>
    [RelayCommand]
    private async Task ReplayFromEndAsync() => await ReloadAtAsync(0);

    private VideoConnectorViewModel? EndConnector => Connector as VideoConnectorViewModel;

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
        LoadEndRecommendations();
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
    }

    /// <summary>
    /// 相关推荐直接取播放器里已有的推荐分区，卡片样式也用现成的那套.
    /// </summary>
    private void LoadEndRecommendations()
    {
        if (EndRecommendations.Count > 0)
        {
            return;
        }

        var section = EndConnector?.Sections?
            .OfType<VideoPlayerRecommendSectionDetailViewModel>()
            .FirstOrDefault();
        if (section?.Items is not { Count: > 0 } items)
        {
            return;
        }

        foreach (var item in items.Take(10))
        {
            EndRecommendations.Add(item);
        }
    }

    private static string FormatEndCount(double value)
        => value >= 10000
            ? $"{value / 10000:F1}万"
            : Math.Round(value).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
}



