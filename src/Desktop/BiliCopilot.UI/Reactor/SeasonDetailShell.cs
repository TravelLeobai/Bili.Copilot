// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Favorites;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Controls;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 合集内视频页外壳：顶栏（合集名 + 订阅按钮 + 播放全部 + 刷新）+ 视频网格.
/// 布局照个人空间那套来，网格里的卡片仍是原来的 XAML 视频卡片.
/// </summary>
public sealed class SeasonDetailShell : Component
{
    private readonly VideoFavoriteFolder _folder;
    private readonly UgcSeasonFavoriteSectionDetailViewModel _section;
    private readonly UgcFavoriteBody _body = new();
    private readonly AppViewModel _appViewModel;
    private readonly SeasonSubscriptionService _subscriptionService;
    private bool _isSubscribed = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonDetailShell"/> class.
    /// </summary>
    /// <param name="folder">合集信息.</param>
    /// <param name="favoriteService">收藏服务.</param>
    /// <param name="appViewModel">应用视图模型.</param>
    /// <param name="subscriptionService">订阅服务.</param>
    public SeasonDetailShell(
        VideoFavoriteFolder folder,
        IFavoriteService favoriteService,
        AppViewModel appViewModel,
        SeasonSubscriptionService subscriptionService)
    {
        _folder = folder;
        _appViewModel = appViewModel;
        _subscriptionService = subscriptionService;
        _section = new UgcSeasonFavoriteSectionDetailViewModel(folder, favoriteService);
        _body.ViewModel = _section;
        _section.TryFirstLoadCommand.Execute(default);
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = UseObservableTree(_section);
        var countText = vm.TotalCount > 0 ? $"共 {vm.TotalCount} 个视频" : "合集";

        return Grid(
            [GridSize.Star()],
            [GridSize.Px(56), GridSize.Px(1), GridSize.Star()],
            BuildHeader(countText).Grid(row: 0),
            ViewLaterShell.Divider().Grid(row: 1),
            ViewLaterShell.Host(_body, control => control.ViewModel = _section, _section).Grid(row: 2));
    }

    private Element BuildHeader(string countText)
        => Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Star()],
            HStack(
                8,
                TextBlock(string.IsNullOrEmpty(_folder.Title) ? "合集" : _folder.Title)
                    .FontSize(15)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .VAlign(VerticalAlignment.Center),
                TextBlock(countText)
                    .FontSize(12)
                    .Opacity(0.6)
                    .VAlign(VerticalAlignment.Center))
                .Margin(16, 0, 0, 0)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),
            TextBlock(string.Empty).Grid(row: 0, column: 1),
            HStack(
                8,
                BuildSubscribeButton(),
                BuildPlayAllButton(),
                BuildRefreshButton())
                .Margin(0, 0, 12, 0)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 2));

    private Element BuildSubscribeButton()
        => new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Content = _isSubscribed ? "已订阅" : "订阅",
                    Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "订阅合集");
                button.Click += async (_, _) =>
                {
                    var target = !_isSubscribed;
                    var ok = await _subscriptionService.SetSubscribedAsync(_folder.Id, target);
                    if (ok)
                    {
                        _isSubscribed = target;
                        button.Content = target ? "已订阅" : "订阅";
                    }
                    else
                    {
                        _appViewModel.ShowTipCommand.Execute(("切换订阅失败，请稍后重试", InfoType.Warning));
                    }
                };
                return button;
            },
            _ => { })
            .WithKey("season-subscribe");

    private Element BuildPlayAllButton()
        => new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Content = "播放全部",
                    Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "播放全部");
                button.Click += (_, _) => PlayAll();
                return button;
            },
            _ => { })
            .WithKey("season-play-all");

    private Element BuildRefreshButton()
        => new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Content = "刷新",
                    Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "刷新");
                button.Click += (_, _) => _section.RefreshCommand.Execute(default);
                return button;
            },
            _ => { })
            .WithKey("season-refresh");

    private void PlayAll()
    {
        var videos = _section.Items.Select(p => p.Data).Where(p => p is not null).ToList();
        if (videos.Count == 0)
        {
            _appViewModel.ShowTipCommand.Execute(("这个合集还没有视频", InfoType.Warning));
            return;
        }

        var first = videos[0]!;
        var snapshot = new MediaSnapshot(first)
        {
            Playlist = videos.Skip(1).Select(p => new MediaSnapshot(p!)).ToList(),
            Type = BiliMediaType.Video,
        };
        _appViewModel.OpenPlayerCommand.Execute(snapshot);
    }
}

