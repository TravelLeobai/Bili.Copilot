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
    private bool _isCompact;

    /// <summary>
    /// 面板是否处于折叠态（只剩右侧展开条）.
    /// </summary>
    public bool IsCollapsed => _isCollapsed;

    /// <summary>
    /// 面板实际占用的宽度：折叠时为 0，展开时取实测宽度（量不到就用 378 兜底）.
    /// </summary>
    public double EffectiveWidth
        => _isCollapsed ? 0d : (ActualWidth > 1 ? ActualWidth : 378d);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _insetTimer;
    private int _insetRetryLeft;

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
        // 窗口尺寸变化后 mpv 会被重置成铺满，这里按当前状态再让一次；
        // 它是在缩放结束后才重置，所以再补一次延迟应用，否则松手后画面会消失.
        ApplySurfaceInset();
        ScheduleInsetRetry();
    }

    /// <summary>
    /// 缩放结束后再应用一次画面让位：mpv 在窗口尺寸稳定后会把自己铺满，
    /// 只应用一次会被它覆盖（表现就是拖完窗口画面只剩一条）.
    /// </summary>
    private void ScheduleInsetRetry()
    {
        _insetTimer ??= DispatcherQueue.CreateTimer();
        _insetTimer.Interval = TimeSpan.FromMilliseconds(220);
        _insetTimer.IsRepeating = true;
        _insetTimer.Tick -= OnInsetTimerTick;
        _insetTimer.Tick += OnInsetTimerTick;
        // mpv 在缩放结束后才把自己铺满，单次重试会被它盖掉，连补几次直到稳定.
        _insetRetryLeft = 5;
        _insetTimer.Start();
    }

    private void OnInsetTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        ApplySurfaceInset();
        _insetRetryLeft--;
        if (_insetRetryLeft <= 0)
        {
            sender.Stop();
        }
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

    /// <summary>
    /// 窄窗时切成「贴着底部、占满宽度」的紧凑布局；宽窗恢复 360 宽侧栏.
    /// </summary>
    /// <param name="compact">是否紧凑.</param>
    public void SetCompact(bool compact)
    {
        _isCompact = compact;
        ApplyCompactState();
        CompactStreamSelector.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        RefreshInset();
    }

    /// <summary>
    /// 让外层改完高度后重新算一次让位.
    /// </summary>
    public void RefreshInset()
    {
        ApplySurfaceInset();
        ScheduleInsetRetry();
    }

    private void ApplyCompactState()
    {
        var width = _isCompact ? double.NaN : 360d;
        HeaderBar.Width = width;
        PanelRoot.Width = width;
        ExpandButton.HorizontalAlignment = _isCompact ? HorizontalAlignment.Right : HorizontalAlignment.Right;
    }

    private void OnCompactStreamFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout || ViewModel is null)
        {
            return;
        }

        flyout.Items.Clear();
        foreach (var option in ViewModel.LiveStreamOptions)
        {
            var item = new MenuFlyoutItem { Text = option.DisplayText };
            var captured = option;
            item.Click += (_, _) => ViewModel.SwitchLiveStreamCommand.Execute(captured);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "还没有可用线路", IsEnabled = false });
        }
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
        // 顶栏（含关闭、刷新）只在展开态出现，收起后只留右侧那根箭头.
        HeaderBar.Visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
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

        var scale = XamlRoot?.RasterizationScale ?? 1d;
        if (_isCompact)
        {
            // 紧凑模式：面板压在下面，画面高度让出来，右侧不再让位.
            var bottom = _isCollapsed || ViewModel?.IsLivePanelNarrow != true
                ? 0d
                : Math.Max(0, ActualHeight);
            Services.MpvSurfaceService.ApplyInset(handle, 0, bottom, scale);
            if (ViewModel is not null)
            {
                ViewModel.LivePanelInset = 0;
            }

            return;
        }

        // 用固定常量算让位：面板宽度是写死的 360（+18 间距），
        // 用 ActualWidth 会在紧凑/宽版切换瞬间取到过期或拉伸后的宽度，导致整块覆盖层被挤窄.
        // 比覆盖层让位再少 8：画面往面板底下压一点，盖住接缝（否则 mpv 按 378、
        // 覆盖层按实测宽度算，两者差值会露出白条）.
        var inset = _isCollapsed ? 0d : Math.Max(0d, Services.MpvSurfaceService.LivePanelWidth - 8d);
        Services.MpvSurfaceService.ApplyInset(handle, inset, 0, scale);

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




