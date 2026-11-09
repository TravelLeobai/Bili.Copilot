// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Animation;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.WinUIKernel.Share.Base;
using WinUIEx;

namespace BiliCopilot.UI.Forms;

/// <summary>
/// 初始窗口.
/// </summary>
public sealed partial class StartupWindow : WindowBase
{
    private bool _isFirstActivated = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupWindow"/> class.
    /// </summary>
    public StartupWindow()
    {
        InitializeComponent();
        Services.StartupTrace.Mark("StartupWindow:ctor:begin");
        IsMaximizable = false;
        IsMinimizable = false;
        IsResizable = false;
        AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);

        Width = 720;
        Height = 460;

        Title = ResourceToolkit.GetLocalizedString(StringNames.AppName);

        this.CenterOnScreen();
        Services.StartupTrace.Mark("StartupWindow:shown");
        this.Get<Microsoft.UI.Dispatching.DispatcherQueue>().TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => Services.StartupTrace.MarkReady());

        // 登录窗口也算"启动成功"：Token 读不出来时应用就走这条路，
        // 不能让它被当成"没起来".
        this.SetIcon("Assets/logo.ico");
        this.SetTitleBar(TitleBar);
        this.Get<AppViewModel>().Windows.Add(this);

        RootFrame.Navigate(typeof(StartupPage), default, new SuppressNavigationTransitionInfo());

        Activated += OnActivated;
        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        var localToken = this.Get<IBiliTokenResolver>().GetToken();
        if (localToken is null)
        {
            this.Get<StartupPageViewModel>().ExitCommand.Execute(default);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isFirstActivated)
        {
            _isFirstActivated = false;
            return;
        }

        this.Get<StartupPageViewModel>().ReloadQRCodeCommand.Execute(default);
    }
}
