// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Controls;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Pages.Overlay;

/// <summary>
/// 合集内视频页面.
/// 外壳由 Reactor 渲染（合集名 / 订阅 / 播放全部 / 刷新），内容网格复用收藏页的视频卡片.
/// </summary>
public sealed partial class SeasonDetailPage : Page, IParameterPage
{
    private ReactorHostControl? _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonDetailPage"/> class.
    /// </summary>
    public SeasonDetailPage() => InitializeComponent();

    /// <inheritdoc/>
    public void SetParameter(object? parameter)
    {
        if (parameter is not VideoFavoriteFolder folder)
        {
            return;
        }

        try
        {
            Mount(folder);
        }
        catch (Exception ex)
        {
            Services.StartupTrace.Fail("SeasonDetailPage.SetParameter", ex);
        }
    }

    private void Mount(VideoFavoriteFolder folder)
    {
        var favoriteService = this.Get<IFavoriteService>();
        var appViewModel = this.Get<AppViewModel>();
        var cookies = this.Get<IBiliCookiesResolver>();
        var subscriptionService = new SeasonSubscriptionService(cookies);

        // 浮层命中同类型页面时只调 SetParameter、不卸载（OverlayFrame.NavigateTo 分支），
        // 而这里每次都新建 Host：不先拆旧的，RootGrid 会一层层叠 Host，
        // 旧外壳（SeasonDetailShell + 它的控件与订阅）也一直挂着.
        _host?.Dispose();
        RootGrid.Children.Clear();

        _host = new ReactorHostControl();
        RootGrid.Children.Add(_host);
        _host.Mount(new SeasonDetailShell(folder, favoriteService, appViewModel, subscriptionService));
    }
}


