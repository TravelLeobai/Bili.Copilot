// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 直播分区顶部的 UP 主过滤项.
/// </summary>
public sealed partial class LiveSectionUperViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveSectionUperViewModel"/> class.
    /// </summary>
    /// <param name="userId">UP 主 id；空表示"全部".</param>
    /// <param name="name">显示名.</param>
    /// <param name="avatar">头像地址.</param>
    /// <param name="selectAction">点击时回调.</param>
    public LiveSectionUperViewModel(string userId, string name, Uri? avatar, Action<LiveSectionUperViewModel> selectAction)
    {
        UserId = userId;
        Name = name;
        Avatar = avatar;
        _selectAction = selectAction;
        SelectCommand = new RelayCommand(() => _selectAction?.Invoke(this));
    }

    private readonly Action<LiveSectionUperViewModel>? _selectAction;

    /// <summary>
    /// UP 主 id，空表示全部.
    /// </summary>
    public string UserId { get; }

    /// <summary>
    /// 显示名.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 头像地址（BasicCoverImage 只吃 Uri，传字符串会在 WinRT 投影里 E_INVALIDARG 崩掉）.
    /// </summary>
    public Uri? Avatar { get; }

    /// <summary>
    /// 是否是"全部".
    /// </summary>
    public bool IsTotal => string.IsNullOrEmpty(UserId);

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// 选中命令.
    /// </summary>
    public IRelayCommand SelectCommand { get; }
}

/// <summary>
/// 直播分区里的一张卡片：可能是正在直播的房间，也可能是直播回放视频.
/// </summary>
public sealed partial class LiveSectionCardViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveSectionCardViewModel"/> class.
    /// </summary>
    /// <param name="title">标题.</param>
    /// <param name="cover">封面.</param>
    /// <param name="authorName">UP 主名.</param>
    /// <param name="authorId">UP 主 id.</param>
    /// <param name="badge">角标文案.</param>
    /// <param name="isLive">是否是直播.</param>
    /// <param name="detail">副标题（人气 / 播放量等）.</param>
    /// <param name="avatar">主播/UP 主头像.</param>
    public LiveSectionCardViewModel(
        string title,
        string cover,
        string authorName,
        string authorId,
        string badge,
        bool isLive,
        string detail,
        Uri? avatar = default)
    {
        Avatar = avatar;
        Title = title;
        Cover = cover;
        AuthorName = authorName;
        AuthorId = authorId;
        Badge = badge;
        IsLive = isLive;
        Detail = detail;
    }

    /// <summary>
    /// 标题.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// 封面地址.
    /// </summary>
    public string Cover { get; }

    /// <summary>
    /// 封面（供 BasicCoverImage 绑定，只吃 Uri）.
    /// </summary>
    public Uri? CoverUri => Uri.TryCreate(Cover, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>
    /// 主播/UP 主头像.
    /// </summary>
    public Uri? Avatar { get; }

    /// <summary>
    /// 用浏览器打开的地址.
    /// </summary>
    public string BrowserUrl => IsLive
        ? $"https://live.bilibili.com/{RoomId}"
        : string.IsNullOrEmpty(Bvid) ? string.Empty : $"https://www.bilibili.com/video/{Bvid}";

    /// <summary>
    /// UP 主名.
    /// </summary>
    public string AuthorName { get; }

    /// <summary>
    /// UP 主 id.
    /// </summary>
    public string AuthorId { get; }

    /// <summary>
    /// 角标文案.
    /// </summary>
    public string Badge { get; set; }

    /// <summary>
    /// 副标题.
    /// </summary>
    public string Detail { get; }

    /// <summary>
    /// 是否是直播.
    /// </summary>
    public bool IsLive { get; }

    /// <summary>
    /// 是否是切片（用户指定的切片 UP 主投稿）.
    /// </summary>
    public bool IsClip { get; set; }

    /// <summary>
    /// 是否是直播回放.
    /// </summary>
    public bool IsReplay => !IsLive && !IsClip;

    /// <summary>
    /// 直播房间号（直播卡片才有）.
    /// </summary>
    public long RoomId { get; set; }

    /// <summary>
    /// 视频 BV 号（回放卡片才有）.
    /// </summary>
    public string Bvid { get; set; } = string.Empty;

    /// <summary>
    /// 播放.
    /// </summary>
    [RelayCommand]
    private void Play()
    {
        var player = this.Get<AppViewModel>();
        if (IsLive)
        {
            var identifier = new MediaIdentifier(RoomId.ToString(), Title, default);
            var user = string.IsNullOrEmpty(AuthorId)
                ? default!
                : new UserProfile(AuthorId, AuthorName, default!);
            player.OpenPlayerCommand.Execute(new MediaSnapshot(new LiveInformation(identifier, user)));
        }
        else if (!string.IsNullOrEmpty(Bvid))
        {
            var identifier = new MediaIdentifier(Bvid, Title, default);
            player.OpenPlayerCommand.Execute(new MediaSnapshot(new VideoInformation(identifier, default)));
        }
    }
}







