// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;

namespace BiliCopilot.UI.Controls.Comment;

/// <summary>
/// 文章阅读器评论面板.
/// </summary>
public sealed partial class CommentOverlayPanel : CommentMainPanelBase
{
    private ReactorHostControl? _reactorHost;
    private CommentMainViewModel? _mountedViewModel;

    /// <summary>
    /// <see cref="IsPanelOpened"/> 依赖属性.
    /// </summary>
    public static readonly DependencyProperty IsPanelOpenedProperty =
        DependencyProperty.Register(nameof(IsPanelOpened), typeof(bool), typeof(CommentOverlayPanel), new PropertyMetadata(default));

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentOverlayPanel"/> class.
    /// </summary>
    public CommentOverlayPanel() => InitializeComponent();

    /// <summary>
    /// 面板可见性.
    /// </summary>
    /// <remarks>
    /// 这个属性归控件自己管：点遮罩收起时只改它，不回调 ViewModel。
    /// 之前试过把点击结果同步回 ViewModel，结果是在点击事件里触发重绘、把面板自己从
    /// 可视树拆掉，直接崩溃；而且 ViewModel 那边一旦把它推回 true，面板就自己弹回来.
    /// </remarks>
    public bool IsPanelOpened
    {
        get => (bool)GetValue(IsPanelOpenedProperty);
        set => SetValue(IsPanelOpenedProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnControlLoaded() => MountPanel(ViewModel);

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        _reactorHost?.Dispose();
        _reactorHost = null;
        _mountedViewModel = null;
        CommentHostGrid.Children.Clear();
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(CommentMainViewModel? oldValue, CommentMainViewModel? newValue)
        => MountPanel(newValue);

    private void MountPanel(CommentMainViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(viewModel, _mountedViewModel))
        {
            return;
        }

        _reactorHost?.Dispose();
        CommentHostGrid.Children.Clear();

        _mountedViewModel = viewModel;
        _reactorHost = new ReactorHostControl();
        CommentHostGrid.Children.Add(_reactorHost);
        _reactorHost.Mount(new CommentPanel(viewModel));
    }

    private void OnHolderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // 遮罩只在面板打开时可见，所以这里一定是「关闭」.
        // 只动自己的可见性，不碰 ViewModel：碰了就会在事件派发途中触发重绘.
        IsPanelOpened = false;
    }
}
