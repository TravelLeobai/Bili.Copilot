// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Settings;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Richasy.WinUIKernel.Share.Toolkits;
using Microsoft.UI.Xaml.Controls;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 使用 Reactor 重写的设置页面外壳.
/// 分组标题与滚动容器由 Reactor 声明式渲染，具体设置项复用原有设置控件.
/// </summary>
public sealed class SettingsShell : Component
{
    private readonly SettingsPageViewModel _viewModel;
    private readonly List<FrameworkElement> _appControls = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsShell"/> class.
    /// </summary>
    public SettingsShell(SettingsPageViewModel viewModel)
    {
        _viewModel = viewModel;
        _appControls.AddRange(new FrameworkElement[]
        {
            new PlayerControlSettingControl(),
            new PlayerBehaviorSettingControl(),
            new MpvExtraSettingControl(),
            new ScreenshotSettingControl(),
            new DownloadSettingControl(),
            new ThemeSettingControl(),
            new LayoutSettingSection(),
            new CacheSettingControl(),
            new NavigationSettingControl(),
            new ScrollSettingControl(),
            new NotificationSettingControl(),
            new LoggerSettingControl(),
            new AboutSettingControl(),
            new ProxySettingControl(),
            new AdSkipSettingControl(),
            new QualityUnlockSettingControl(),
        });
    }

    /// <inheritdoc/>
    public override Element Render()
    {
        UseObservableTree(_viewModel);

        var header = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Star()],
            TextBlock("设置").SemiBold().FontSize(18).VAlign(VerticalAlignment.Center))
            .Height(56)
            .Padding(16, 0);

        // 用类型找控件，别再靠下标：删掉一个分区后下标会整体错位，进设置页直接越界崩溃.
        var children = new List<Element>
        {
            SectionTitle("播放器"),
            Control<PlayerControlSettingControl>(),
            Control<PlayerBehaviorSettingControl>(),
            Control<MpvExtraSettingControl>(),
            Control<ScreenshotSettingControl>(),
            Control<DownloadSettingControl>(),
            SectionTitle("自动化"),
            Control<AdSkipSettingControl>(),
            SectionTitle("网络与加速"),
            Control<ProxySettingControl>(),
            Control<QualityUnlockSettingControl>(),
            SectionTitle("通用"),
            Control<ThemeSettingControl>(),
            Control<LayoutSettingSection>(),
            Control<CacheSettingControl>(),
            Control<NavigationSettingControl>(),
            Control<ScrollSettingControl>(),
            Control<NotificationSettingControl>(),
            Control<LoggerSettingControl>(),
            SectionTitle("关于"),
            Control<AboutSettingControl>(),
        };

        var body = ScrollView(VStack(4, [.. children]).Padding(16, 0));

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            header.Grid(row: 0),
            body.Grid(row: 1));
    }
    private static Element SectionTitle(string text)
        => TextBlock(text).FontSize(13).SemiBold().Margin(0, 12, 0, 4);

    private Element Control<TControl>()
        where TControl : FrameworkElement
        => new XamlHostElement(
            () => _appControls.OfType<TControl>().First(),
            _ => { });
}



