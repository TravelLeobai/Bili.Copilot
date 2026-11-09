// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 订阅合集条目视图模型.
/// </summary>
public sealed partial class SeasonSubscriptionItemViewModel : ViewModelBase<VideoFavoriteFolder>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonSubscriptionItemViewModel"/> class.
    /// </summary>
    /// <param name="data">合集信息.</param>
    public SeasonSubscriptionItemViewModel(VideoFavoriteFolder data)
        : base(data)
    {
        Title = string.IsNullOrEmpty(data.Title) ? "未命名合集" : data.Title;
        CoverUri = data.Cover?.Uri;
        Avatar = data.User?.Avatar?.Uri;
        AuthorName = data.User?.Name ?? string.Empty;
        CountText = data.TotalCount > 0 ? $"共 {data.TotalCount} 个视频" : "合集";
    }

    /// <summary>
    /// 标题.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// 封面.
    /// </summary>
    public Uri? CoverUri { get; }

    /// <summary>
    /// 作者头像.
    /// </summary>
    public Uri? Avatar { get; }

    /// <summary>
    /// 作者名.
    /// </summary>
    public string AuthorName { get; }

    /// <summary>
    /// 视频数文案.
    /// </summary>
    public string CountText { get; }

    /// <summary>
    /// 打开这个合集.
    /// </summary>
    [RelayCommand]
    private void Open()
    {
        var navigation = this.Get<NavigationViewModel>();
        navigation.NavigateToOver(typeof(SeasonDetailPage), Data);
    }
}
