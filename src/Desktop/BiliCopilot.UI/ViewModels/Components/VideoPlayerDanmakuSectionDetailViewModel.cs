// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.User;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频播放页弹幕列表区块视图模型.
/// </summary>
public sealed partial class VideoPlayerDanmakuSectionDetailViewModel : ViewModelBase, IPlayerSectionDetailViewModel
{
    private const int MaxItems = 400;
    private const int MaxResolvedUsers = 40;

    private readonly string _aid;
    private readonly string _cid;
    private readonly DanmakuListLoader _loader = new();
    private readonly HashSet<string> _blockedContents = [];
    private readonly Dictionary<string, (string Name, Uri? Avatar, int Level)> _userCache = [];
    private bool _isInitialized;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPlayerDanmakuSectionDetailViewModel"/> class.
    /// </summary>
    public VideoPlayerDanmakuSectionDetailViewModel(VideoConnectorViewModel page, string aid, string cid)
    {
        Page = page;
        _aid = aid;
        _cid = cid;
    }

    /// <summary>
    /// 视频播放页视图模型.
    /// </summary>
    public VideoConnectorViewModel Page { get; }

    /// <inheritdoc/>
    public string Title { get; } = "弹幕";

    /// <summary>
    /// 弹幕列表.
    /// </summary>
    public ObservableCollection<DanmakuListItemViewModel> Items { get; } = [];

    /// <inheritdoc/>
    [RelayCommand]
    private async Task TryFirstLoadAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await LoadItemsAsync();
    }

    /// <summary>
    /// 跳转到弹幕对应的播放时间.
    /// </summary>
    [RelayCommand]
    private async Task SeekAsync(DanmakuListItemViewModel item)
    {
        if (item is null || Page?.Parent is null)
        {
            return;
        }

        await Page.Parent.ChangePositionAsync(item.PositionSeconds);
    }

    /// <summary>
    /// 屏蔽弹幕.
    /// </summary>
    [RelayCommand]
    private void Block(DanmakuListItemViewModel item)
    {
        if (item is null)
        {
            return;
        }

        _blockedContents.Add(item.Content);
        item.IsBlocked = true;
        Items.Remove(item);
        foreach (var sameContent in Items.Where(p => p.Content == item.Content).ToList())
        {
            sameContent.IsBlocked = true;
            Items.Remove(sameContent);
        }

        IsEmpty = Items.Count == 0;
    }

    private async Task LoadItemsAsync()
    {
        IsLoading = true;
        try
        {
            var entries = await _loader.LoadAsync(_cid);
            var filtered = entries
                .Where(p => !_blockedContents.Contains(p.Content))
                .OrderBy(p => p.PositionSeconds)
                .Take(MaxItems)
                .ToList();

            Items.Clear();
            foreach (var entry in filtered)
            {
                Items.Add(new DanmakuListItemViewModel(entry.Uid, entry.Content, entry.PositionSeconds));
            }

            IsEmpty = Items.Count == 0;
        }
        catch (Exception ex)
        {
            this.Get<ILogger<VideoPlayerDanmakuSectionDetailViewModel>>().LogError(ex, "加载弹幕列表失败");
            IsEmpty = Items.Count == 0;
        }
        finally
        {
            IsLoading = false;
        }

        await ResolveUsersAsync();
    }

    private async Task ResolveUsersAsync()
    {
        // B 站弹幕接口返回的 UID 已做过哈希处理，只有纯数字的才可能是真实 UID.
        var pending = Items
            .Where(p => p.IsUserResolved == false)
            .GroupBy(p => p.Uid)
            .Select(p => p.Key)
            .Where(p => !string.IsNullOrEmpty(p) && p != "0" && p.All(char.IsDigit))
            .Take(MaxResolvedUsers)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var userService = this.Get<IUserService>();
        var semaphore = new SemaphoreSlim(4, 4);
        var tasks = pending.Select(async uid =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!_userCache.TryGetValue(uid, out var info))
                {
                    var card = await userService.GetUserInformationAsync(uid);
                    if (card?.Profile?.User is null)
                    {
                        return;
                    }

                    info = (card.Profile.User.Name, card.Profile.User.Avatar?.Uri, card.Profile.Level ?? 0);
                    _userCache[uid] = info;
                }

                var items = Items.Where(p => p.Uid == uid).ToList();
                foreach (var item in items)
                {
                    item.UserName = info.Name;
                    item.Avatar = info.Avatar;
                    item.Level = info.Level;
                    item.IsUserResolved = true;
                }
            }
            catch (Exception ex)
            {
                // 单个用户解析失败不影响列表.
                _ = ex;
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // 没抓到昵称/头像的，直接显示 UID（构造函数里已把 UserName 初始化为 UID）.
    }
}
