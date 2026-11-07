// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;
using Richasy.BiliKernel.Bili.Moment;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 动态页面视图模型.
/// </summary>
public sealed partial class MomentPageViewModel
{
    private readonly IMomentDiscoveryService _momentDiscoveryService;

    [ObservableProperty]
    private List<IMomentSectionDetailViewModel> _sections;

    [ObservableProperty]
    private IMomentSectionDetailViewModel _selectedSection;

    [ObservableProperty]
    private bool _isCommentsOpened;

    /// <summary>
    /// 评论面板的「打开请求」版本号：每点一次评论按钮就加一.
    /// </summary>
    /// <remarks>
    /// 面板开合由控件自己管（点遮罩就是收起），所以不能用一个 bool 双向同步 ——
    /// 那样 ViewModel 会在重绘时把用户刚收起的面板推回打开。
    /// 这里只表达「用户又请求打开了」，宿主看到版本变了就把面板打开.
    /// </remarks>
    [ObservableProperty]
    private int _commentsOpenVersion;

    /// <summary>
    /// 区块初始化完成.
    /// </summary>
    public event EventHandler Initialized;

    /// <summary>
    /// 评论模块.
    /// </summary>
    public CommentMainViewModel CommentModule { get; }
}
