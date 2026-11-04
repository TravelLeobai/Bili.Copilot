// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 直播侧栏宿主：内容由 Reactor 渲染，外部负责贴边停靠，这里负责折叠.
/// </summary>
public sealed partial class LiveSidePanelHost : UserControl
{
    private ReactorHostControl? _reactorHost;
    private PlayerViewModel? _mountedViewModel;
    private bool _isCollapsed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveSidePanelHost"/> class.
    /// </summary>
    public LiveSidePanelHost()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 播放器视图模型.
    /// </summary>
    public PlayerViewModel? ViewModel
    {
        get => (PlayerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// 播放器视图模型依赖属性.
    /// </summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(PlayerViewModel), typeof(LiveSidePanelHost), new PropertyMetadata(null, OnViewModelChanged));

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LiveSidePanelHost)d).MountPanel();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCollapsed = SettingsToolkit.ReadLocalSetting(SettingNames.LivePanelCollapsed, true);
        ApplyCollapsedState();
        MountPanel();
        SizeChanged += OnHostSizeChanged;
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 窗口尺寸变化后 mpv 会被重置成铺满，这里按当前折叠状态再让一次.
        ApplySurfaceInset();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnHostSizeChanged;
        Services.MpvSurfaceService.ApplyRightInset(GetPlayerWindowHandle(), 0, XamlRoot?.RasterizationScale ?? 1d);
        _reactorHost?.Dispose();
        _reactorHost = null;
        _mountedViewModel = null;
        ReactorHost.Children.Clear();
    }

    private void MountPanel()
    {
        if (ViewModel is null || ReferenceEquals(ViewModel, _mountedViewModel))
        {
            return;
        }

        _reactorHost?.Dispose();
        ReactorHost.Children.Clear();

        _mountedViewModel = ViewModel;
        _reactorHost = new ReactorHostControl();
        ReactorHost.Children.Add(_reactorHost);
        _reactorHost.Mount(new LiveSidePanel(ViewModel));
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e) => SetCollapsed(true);

    private void OnExpandClick(object sender, RoutedEventArgs e) => SetCollapsed(false);

    private void SetCollapsed(bool value)
    {
        _isCollapsed = value;
        SettingsToolkit.WriteLocalSetting(SettingNames.LivePanelCollapsed, value);
        ApplyCollapsedState();
    }

    private void ApplyCollapsedState()
    {
        PanelRoot.Visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ExpandButton.Visibility = _isCollapsed ? Visibility.Visible : Visibility.Collapsed;
        // 收起后左侧只剩展开条，收起按钮要跟着藏起来.
        CollapseButton.Visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ApplySurfaceInset();
    }
    /// <summary>
    /// 让 mpv 画面给右侧面板腾地方：展开时画面缩到左边，收起时恢复铺满.
    /// </summary>
    private void ApplySurfaceInset()
    {
        var handle = GetPlayerWindowHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // 只在直播时让位：点播/番剧界面不该被这块面板影响.
        if (ViewModel?.IsLive != true)
        {
            Services.MpvSurfaceService.ApplyRightInset(handle, 0, XamlRoot?.RasterizationScale ?? 1d);
            if (ViewModel is not null)
            {
                ViewModel.LivePanelInset = 0;
            }

            return;
        }

        // 用实际宽度算让位，按钮列宽度改了也不用同步改常量.
        var measured = ActualWidth;
        var inset = _isCollapsed
            ? 0d
            : measured > 40 ? Math.Ceiling(measured) : (double)Services.MpvSurfaceService.LivePanelWidth;
        Services.MpvSurfaceService.ApplyRightInset(handle, inset, XamlRoot?.RasterizationScale ?? 1d);

        // 让覆盖层（进度条、按钮、右上角控件）也跟着缩，避免被面板压住.
        if (ViewModel is not null)
        {
            ViewModel.LivePanelInset = inset;
        }
    }

    private IntPtr GetPlayerWindowHandle()
        => ViewModel?.Window is null ? IntPtr.Zero : ViewModel.Window.Handle;
    private void OnRefreshClick(object sender, RoutedEventArgs e)
        => ViewModel?.RefreshLiveInfoCommand.Execute(default);
}
