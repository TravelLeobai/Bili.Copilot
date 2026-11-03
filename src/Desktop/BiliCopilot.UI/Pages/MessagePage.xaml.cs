// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 消息页面.
/// </summary>
public sealed partial class MessagePage : MessagePageBase
{
    private readonly ReactorHostControl _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessagePage"/> class.
    /// </summary>
    public MessagePage()
    {
        InitializeComponent();
        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new MessageShell(ViewModel));
    }

    /// <inheritdoc/>
    protected override void OnPageLoaded()
        => ViewModel.InitializeCommand.Execute(default);
}

/// <summary>
/// 消息页面基类.
/// </summary>
public abstract class MessagePageBase : LayoutPageBase<MessagePageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessagePageBase"/> class.
    /// </summary>
    protected MessagePageBase() => ViewModel = this.Get<MessagePageViewModel>();
}
