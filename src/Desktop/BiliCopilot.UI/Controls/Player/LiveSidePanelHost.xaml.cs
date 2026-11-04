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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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
    }
}