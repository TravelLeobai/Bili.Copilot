// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Models.Moment;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 动态页面视图模型.
/// </summary>
public sealed partial class MomentPageViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MomentPageViewModel"/> class.
    /// </summary>
    public MomentPageViewModel(
        IMomentDiscoveryService discoveryService,
        CommentMainViewModel comment)
    {
        _momentDiscoveryService = discoveryService;
        CommentModule = comment;
    }

    [RelayCommand]
    private void Initialize()
    {
        if (Sections?.Count > 0)
        {
            return;
        }

        Sections = new List<IMomentSectionDetailViewModel>
        {
            new VideoMomentSectionDetailViewModel(_momentDiscoveryService),
            new ComprehensiveMomentSectionDetailViewModel(_momentDiscoveryService),
            new LiveMomentSectionDetailViewModel(_momentDiscoveryService),
        };

        // 记住上次停在哪一段：老设置只有"是不是视频"，用新键存完整的段类型.
        var lastType = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.LastMomentSectionType, -1);
        var isLastVideoSection = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.LastMomentSectionIsVideo, true);
        var section = lastType >= 0
            ? Sections.FirstOrDefault(p => (int)p.SectionType == lastType)
            : isLastVideoSection ? Sections.First() : Sections.Last();
        SelectSectionCommand.Execute(section ?? Sections.First());
        this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, () => Initialized?.Invoke(this, EventArgs.Empty));
    }

    [RelayCommand]
    private void SelectSection(IMomentSectionDetailViewModel section)
    {
        if (section is null || section == SelectedSection)
        {
            return;
        }

        SelectedSection = section;
        var isVideo = section is VideoMomentSectionDetailViewModel;
        SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastMomentSectionIsVideo, isVideo);
        SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastMomentSectionType, (int)section.SectionType);
        SelectedSection.InitializeCommand.Execute(default);
    }

    [RelayCommand]
    private void ShowComment(MomentInformation data)
    {
        // 打开请求用版本号表达，面板自己负责收起，两边不互相覆盖.
        CommentsOpenVersion++;
        if (CommentModule.Id == data.CommentId)
        {
            return;
        }

        CommentModule.Initialize(data.CommentId, data.CommentType!.Value, Richasy.BiliKernel.Models.CommentSortType.Hot);
        CommentModule.RefreshCommand.Execute(default);
    }

    /// <summary>
    /// 更新动态视频最近打开标记样式.
    /// </summary>
    public void SetVideoOpenedMarkStyle(Models.Constants.MomentVideoOpenedMarkStyle style)
        => Sections?.OfType<VideoMomentSectionDetailViewModel>().FirstOrDefault()?.SetOpenedMarkStyle(style);
}





