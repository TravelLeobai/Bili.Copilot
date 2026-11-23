// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Controls;

namespace BiliCopilot.UI.Controls.Comment;

/// <summary>
/// 评论正文下面的投票入口：一个按钮 + 弹出面板.
/// <para>
/// 详情只在弹出打开时拉一次（<c>Flyout.Opening</c>），列表滚动不会发请求.
/// </para>
/// </summary>
public sealed partial class CommentVoteEntryControl : UserControl
{
    /// <summary>
    /// <see cref="ViewModel"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(CommentVoteViewModel), typeof(CommentVoteEntryControl), new PropertyMetadata(default(CommentVoteViewModel)));

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVoteEntryControl"/> class.
    /// </summary>
    public CommentVoteEntryControl() => InitializeComponent();

    /// <summary>
    /// Gets or sets 要展示的投票.
    /// </summary>
    public CommentVoteViewModel? ViewModel
    {
        get => (CommentVoteViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // 每次打开都重新拉一次：票数和「我投了什么」都可能是别处改过的.
    // 第一次打开时面板还没挂载（Phase 是 Idle），加载由面板自己的 Loaded 负责，
    // 这里不重复发一遍；之后再打开就是纯刷新.
    private void OnFlyoutOpening(object sender, object e)
    {
        if (ViewModel is { Phase: not CommentVotePhase.Idle } viewModel)
        {
            viewModel.LoadCommand.Execute(default);
        }
    }
}
