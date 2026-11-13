// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Player;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class VideoConnectorViewModel : ViewModelBase, IPlayerConnectorViewModel
{
    public VideoConnectorViewModel(PlayerViewModel parent)
    {
        // 性能探针（三级）：构造函数在播放器初始化的同步段里跑，这里记检查点定位耗时来源.
        Services.PerfWidgetMountTrace.Mark("connector-ctor-start");
        Parent = parent;
        _comments = this.Get<CommentMainViewModel>();
        Services.PerfWidgetMountTrace.Mark("comments-vm-ready");
        Downloader = this.Get<DownloadViewModel>();
        Services.PerfWidgetMountTrace.Mark("downloader-vm-ready");
        AI = this.Get<AIViewModel>();
        Services.PerfWidgetMountTrace.Mark("ai-vm-ready");
    }

    public async Task InitializeAsync(MediaSnapshot snapshot, IMpvMediaSourceResolver sourceResolver)
    {
        _snapshot = snapshot;
        ClearView();
        IsLoading = true;
        // 性能探针：详情面板首次加载从这一刻开始计时（探针关闭时是空操作）.
        VideoPlayerInfoSectionDetailViewModel.BeginFirstLoadProbe();
        await LoadItemDetailAsync();
        var prev = FindPrevVideo();
        var next = FindNextVideo();
        var prevName = prev is VideoPart part ? part.Identifier.Title : prev is VideoInformation video ? video.Identifier.Title : default;
        var nextName = next is VideoPart part2 ? part2.Identifier.Title : next is VideoInformation video2 ? video2.Identifier.Title : default;
        var initArgs = new PlaylistInitializedEventArgs(prev != null, next != null, prevName, nextName);
        PlaylistInitialized?.Invoke(this, initArgs);
        IsLoading = false;
    }

    public void InitializeDownloader(List<PlayerFormatInformation> formats)
    {
        Downloader.Clear();
        Downloader.InitializeMetas(
            GetWebLink(),
            formats.AsReadOnly(),
            _view.Parts?.Count > 1 ? _view.Parts.AsReadOnly() : default,
            _view.Parts.IndexOf(_part) + 1);
    }

    public async Task PlayNextAsync()
    {
        if (IsLoading)
        {
            return;
        }

        var next = FindNextVideo();
        if (next is null)
        {
            return;
        }

        if (next is VideoPart part)
        {
            // 不能就地改 _snapshot.PreferPart 再重新初始化：PlayerViewModel 判断"是不是同一个视频"
            // 靠比较快照里的媒体 id，改同一个对象的话前后永远相等，于是走了复用 mpv 的分支，
            // 换了 cid 的片源在旧 vo 上重新 init 会失败 —— 表现就是点「下一集」白屏卡死.
            // 这里另起一个快照，让前后两个快照各自的 part 能被识别出来.
            Parent.InitializeCommand.Execute(CreateSnapshotForPart(part));
            return;
        }
        else if (next is VideoInformation video)
        {
            Parent.InitializeCommand.Execute(new MediaSnapshot(video, IsPrivatePlay) { Playlist = _snapshot.Playlist, Type = BiliMediaType.Video });
        }

        await Task.CompletedTask;
    }

    public async Task PlayPreviousAsync()
    {
        if (IsLoading)
        {
            return;
        }

        var next = FindPrevVideo();
        if (next is null)
        {
            return;
        }

        if (next is VideoPart part)
        {
            // 同上：另起快照，别就地改 _snapshot.
            Parent.InitializeCommand.Execute(CreateSnapshotForPart(part));
            return;
        }
        else if (next is VideoInformation video)
        {
            Parent.InitializeCommand.Execute(new MediaSnapshot(video, IsPrivatePlay) { Playlist = _snapshot.Playlist, Type = BiliMediaType.Video });
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 用当前视频信息另起一个快照，只换分P，其余（播放列表、私密播放、类型）原样带过去.
    /// </summary>
    /// <param name="part">目标分P.</param>
    /// <returns>新快照.</returns>
    private MediaSnapshot CreateSnapshotForPart(VideoPart part)
        => new(_snapshot.Video!, _snapshot.IsPrivate)
        {
            PreferPart = part,
            Playlist = _snapshot.Playlist,
            Type = _snapshot.Type,
            PreferQuality = _snapshot.PreferQuality,
            StartPosition = 0,
        };

    [RelayCommand]
    private void OpenPanel()
        => RequestOpenExtraPanel?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void SelectSection(IPlayerSectionDetailViewModel section)
    {
        if (section is null || section == SelectedSection)
        {
            return;
        }

        SelectedSection = section;
        SelectedSection.TryFirstLoadCommand.Execute(default);
    }

    private void PlayItem(PlaylistMediaViewModel vm)
    {
        if (vm.Data.Video.Identifier.Id == _snapshot.Video.Identifier.Id)
        {
            return;
        }

        NewMediaRequest?.Invoke(this, vm.Data);
    }

    private async Task LoadItemDetailAsync()
    {
        var service = this.Get<IPlayerService>();
        var networkWatch = VideoPlayerInfoSectionDetailViewModel.BeginNetworkProbe();
        try
        {
            try
            {
                // 只把这一次 await 算进网络耗时，后面的填充/切区块不算.
                _view = await service.GetVideoPageDetailAsync(_snapshot.Video.Identifier);
            }
            finally
            {
                VideoPlayerInfoSectionDetailViewModel.EndNetworkProbe(networkWatch);
            }

            _part = FindInitialPart(_snapshot.PreferPart?.Identifier.Id);
            UpdateProperties();
            InitializeView(_view);
            _comments?.Initialize(_view.Information.Identifier.Id, Richasy.BiliKernel.Models.CommentTargetType.Video, Richasy.BiliKernel.Models.CommentSortType.Hot);
            // 性能探针：进 InitializeSections 之前先把挂载窗口打开，万一侧栏是同步挂上来的，
            // "首次渲染"那次投递也还能发生（探针关闭时是空操作）.
            VideoPlayerInfoSectionDetailViewModel.BeginPanelMountProbe();
            InitializeSections();
            // 再把区间起点挪到"区块建完"这一刻：Detail.Panel.Mount 从这里开始算到侧栏 Loaded.
            VideoPlayerInfoSectionDetailViewModel.BeginPanelMountProbe();
            VideoPlayerInfoSectionDetailViewModel.CompleteFirstLoadProbe(Tags?.Count ?? 0);
            AI.InjectVideoAsync(_view, _part);
        }
        catch (Exception ex)
        {
            VideoPlayerInfoSectionDetailViewModel.AbortFirstLoadProbe();
            this.Get<ILogger<VideoConnectorViewModel>>().LogError(ex, "获取视频信息失败");
        }
    }

    private VideoPart? FindInitialPart(string? initialPartId = default)
    {
        if (_view.Parts?.Count == 1)
        {
            return _view.Parts[0];
        }

        VideoPart? part = default;
        if (!string.IsNullOrEmpty(initialPartId))
        {
            part = _view.Parts.FirstOrDefault(p => p.Identifier.Id == initialPartId);
        }

        if (part == null)
        {
            var historyPartId = _view.Progress?.Cid;
            var autoLoadHistory = SettingsToolkit.ReadLocalSetting(SettingNames.AutoLoadHistory, true);
            if (!string.IsNullOrEmpty(historyPartId) && autoLoadHistory)
            {
                part = _view.Parts.FirstOrDefault(p => p.Identifier.Id == historyPartId);
            }
        }

        return part ?? _view.Parts.FirstOrDefault();
    }

    private void UpdateProperties()
    {
        Title = _view.Information.Identifier.Title;
        Subtitle = string.IsNullOrEmpty(_view.Information.GetExtensionIfNotNull<string>(VideoExtensionDataId.Description)) ? _view.Information.Publisher.User.Name : _view.Information.GetExtensionIfNotNull<string>(VideoExtensionDataId.Description);
        Description = _view.Information.GetExtensionIfNotNull<string>(VideoExtensionDataId.Description) ?? string.Empty;
        Cover = _view.Information.Identifier.Cover?.Uri;
        var args = new PlayerInformationUpdatedEventArgs(Title, Subtitle, Cover);
        PropertiesUpdated?.Invoke(this, args);
    }
}


