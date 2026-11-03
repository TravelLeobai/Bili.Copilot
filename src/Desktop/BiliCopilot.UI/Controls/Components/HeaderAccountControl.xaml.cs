// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Xaml;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// 位于标题栏的账户控件.
/// </summary>
public sealed partial class HeaderAccountControl : HeaderAccountControlBase
{
    /// <summary>
    /// 头像尺寸依赖属性.
    /// </summary>
    public static readonly DependencyProperty AvatarSizeProperty =
        DependencyProperty.Register(
            nameof(AvatarSize),
            typeof(double),
            typeof(HeaderAccountControl),
            new PropertyMetadata(28d, OnAvatarSizeChanged));

    /// <summary>
    /// Initializes a new instance of the <see cref="HeaderAccountControl"/> class.
    /// </summary>
    public HeaderAccountControl() => InitializeComponent();

    /// <summary>
    /// 头像边长，默认 28，用于和侧边栏图标对齐时改小.
    /// </summary>
    public double AvatarSize
    {
        get => (double)GetValue(AvatarSizeProperty);
        set => SetValue(AvatarSizeProperty, value);
    }

    /// <summary>
    /// 打开账户浮层.
    /// </summary>
    public void ShowAccountFlyout()
    {
        if (AccountButton.Flyout is not null)
        {
            AccountButton.Flyout.ShowAt(AccountButton);
        }
    }

    private static void OnAvatarSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (HeaderAccountControl)d;
        var size = (double)e.NewValue;
        if (control.Avatar is not null)
        {
            control.Avatar.Width = size;
            control.Avatar.Height = size;
            control.Avatar.CornerRadius = new CornerRadius(size / 2);
        }
    }

    /// <inheritdoc/>
    protected override void OnControlLoaded()
        => ViewModel.InitializeCommand.Execute(default);

    private void OnFlyoutOpened(object sender, object e)
    {
        ViewModel.UpdateCommunityInformationCommand.Execute(default);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ViewModel.IsFlyoutOpened = true);
    }

    private void OnFlyoutClosed(object sender, object e)
    {
        ViewModel.IsFlyoutOpened = false;
    }
}

/// <summary>
/// 位于标题栏的账户控件基类.
/// </summary>
public abstract class HeaderAccountControlBase : LayoutUserControlBase<AccountViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HeaderAccountControlBase"/> class.
    /// </summary>
    protected HeaderAccountControlBase() => ViewModel = this.Get<AccountViewModel>();
}
