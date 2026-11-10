// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Core;

public sealed partial class PlayerOverlay : PlayerControlBase
{
    private const int MediaTransportControlsHeight = 160;
    private const int HeaderBarHeight = 70;
    private readonly DispatcherQueueTimer _controlTimer;
    private readonly DispatcherQueueTimer _cursorTimer;
    private bool _isTouch;
    private bool _isInTransportControls;
    private bool _isInTitleBar;
    private Point _lastPointerPosition;
    private bool _isDraggingStatistics;
    private Point _statisticsDragStart;
    private Point _statisticsOffsetStart;

    public PlayerOverlay()
    {
        InitializeComponent();
        _controlTimer = DispatcherQueue.CreateTimer();
        _controlTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _controlTimer.Tick += OnControlTimerTick;
        _cursorTimer = DispatcherQueue.CreateTimer();
        _cursorTimer.Interval = TimeSpan.FromMilliseconds(500);
        _cursorTimer.Tick += OnCursorTimerTick;
        SizeChanged += OnOverlaySizeChanged;
    }


    /// <summary>
    /// 自适应触发切换直播面板排布：窄窗压底部，宽窗贴右侧.
    /// 这里只负责同步「宿主紧凑模式」与 mpv 画面让位，位置本身由 VisualState 的 Setter 管.
    /// </summary>
    private void OnLivePanelLayoutStateChanged(object sender, VisualStateChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ApplyLivePanelGeometry(e.NewState?.Name == "NarrowLivePanel");
    }

    /// <summary>
    /// 窄窗：面板高度 = 窗口高 − 16:9 画面高，画面贴顶、面板无缝接在下面；
    /// 宽窗：面板恢复贴右竖排.
    /// </summary>
    private void ApplyLivePanelGeometry(bool narrow)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SetLivePanelNarrow(narrow, ActualWidth, ActualHeight);
        LivePanelControl.Height = narrow ? ViewModel.LivePanelBottomInset : double.NaN;
        LivePanelControl.SetCompact(narrow && ViewModel.IsLive);
        LivePanelControl.RefreshInset();
        ApplyLiveHeaderAlignment();
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel?.IsLivePanelNarrow == true)
        {
            ApplyLivePanelGeometry(true);
        }
    }

    public void ShowTip(string text, InfoType type = InfoType.Error)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var popup = new TipPopup() { Text = text };
            TipContainer.Visibility = Visibility.Visible;
            TipContainer.Children.Add(popup);
            await popup.ShowAsync(type);
            TipContainer.Children.Remove(popup);
            TipContainer.Visibility = Visibility.Collapsed;

        });
    }

    /// <summary>
    /// 直播时不显示 CDN 节点指示器.
    /// </summary>
    /// <param name="isLive">是否直播.</param>
    /// <returns>可见性.</returns>
    internal static Visibility GetCdnVisibility(bool isLive)
        => isLive ? Visibility.Collapsed : Visibility.Visible;

    internal static Visibility GetSkipPromptVisibility(bool isVisible)
        => isVisible ? Visibility.Visible : Visibility.Collapsed;

    internal static Visibility IsTopMostButtonVisible(bool isFullScreen, bool isAot)
        => (isFullScreen || isAot) ? Visibility.Collapsed : Visibility.Visible;

    protected override void OnControlLoaded()
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyLiveHeaderAlignment();
        ViewModel.WarningOccurred += OnWarningOccurred;
        ViewModel.RequestShowNextTip += OnRequestShowNextTip;
        ViewModel.RequestHideNextTip += OnRequestHideNextTip;
    }

    protected override void OnControlUnloaded()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.WarningOccurred -= OnWarningOccurred;
        ViewModel.RequestShowNextTip -= OnRequestShowNextTip;
        ViewModel.RequestHideNextTip -= OnRequestHideNextTip;
        _controlTimer.Tick -= OnControlTimerTick;
        _controlTimer.Stop();
        _cursorTimer.Tick -= OnCursorTimerTick;
        _cursorTimer.Stop();
        SourceListView?.ItemsSource = default;
        SizeChanged -= OnOverlaySizeChanged;
    }

    private void OnRequestHideNextTip(object? sender, EventArgs e)
        => NextTip?.Hide();

    private void OnRequestShowNextTip(object? sender, EventArgs e)
        => NextTip?.Show();

    private void OnWarningOccurred(object? sender, string e)
    {
        System.Diagnostics.Debug.WriteLine($"Warning: {e}");
        //var popup = new TipPopup() { Text = e };
        //RootGrid.Children.Add(popup);
        //await popup.ShowAsync(InfoType.Warning);
        //RootGrid.Children.Remove(popup);
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        if (position.Equals(_lastPointerPosition))
        {
            // If the pointer hasn't moved, do nothing.
            return;
        }

        _lastPointerPosition = position;
        ViewModel.Window?.ShowCursor();
        _isTouch = e.Pointer.PointerDeviceType is Microsoft.UI.Input.PointerDeviceType.Touch or Microsoft.UI.Input.PointerDeviceType.Pen;
        _cursorTimer.Stop();
        _controlTimer.Stop();
        TestRect.Visibility = Visibility.Visible;
        if (!_isTouch && !ViewModel.IsExtraPanelVisible)
        {
            _isInTitleBar = position.Y <= HeaderBarHeight && position.Y >= 0;
            var liveBottomInset = ViewModel?.IsLivePanelNarrow == true ? ViewModel.LivePanelBottomInset : 0d;
        _isInTransportControls = position.Y >= ActualHeight - liveBottomInset - MediaTransportControlsHeight;
            if (_isInTitleBar || _isInTransportControls)
            {
                // If the pointer is in the title bar or transport controls, show controls immediately.
                _cursorTimer.Start();
                ViewModel.IsControlsVisible = true;
            }
            else
            {
                _controlTimer.Start();
                if (!ViewModel.IsEnd)
                {
                    ViewModel.IsControlsVisible = false;
                }
            }
        }
    }

    /// <summary>
    /// 直播时把标题栏右侧那组按钮往右顶到画面右缘：标题栏 Footer 有效区会被系统
    /// 最小化/最大化/关闭按钮占掉约 140px，用负右边距抵消；点播保持原样.
    /// </summary>
    private void OnOverlayLoaded(object sender, RoutedEventArgs e)
    {
        // 模板应用完才有 FooterContainer，这里再对齐一次.
        ApplyLiveHeaderAlignment();
    }

    private void ApplyLiveHeaderAlignment()
    {
        if (FooterContainer is null)
        {
            return;
        }

        // 窄版：视频区右上那排按钮整排隐藏（窄窗塞不下，也不该压画面）.
        // 宽版：顶栏整条按面板宽度缩进来，右侧按钮才不会跑到面板底下.
        var narrow = ViewModel.IsLivePanelNarrow;
        var panelOnRight = ViewModel.IsLive && !narrow && ViewModel.LivePanelInset > 1;

        // 折叠/无面板时也要给窗口按钮（最小化/最大化/关闭）让位，否则顶栏最右按钮会藏在它们后面.
        const double CaptionButtonsWidth = 150d;
        // 面板本身有 16 的内边距，扣掉后顶栏最右按钮正好贴着面板左边缘（视觉上对齐，不留缝）.
        var rightInset = panelOnRight ? Math.Max(0d, ViewModel.LivePanelInset - 16d) : (narrow ? 0d : CaptionButtonsWidth);
        FooterContainer.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        HeaderBarHost.Margin = rightInset > 0 ? new Thickness(0, 0, rightInset, 0) : new Thickness(0);
        FooterContainer.Margin = new Thickness(0);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.IsLive) or nameof(ViewModel.LivePanelInset) or nameof(ViewModel.IsLivePanelNarrow))
        {
            ApplyLiveHeaderAlignment();
        }
        else if (e.PropertyName == nameof(ViewModel.IsControlsVisible))
        {
            if (!ViewModel.IsControlsVisible && !_controlTimer.IsRunning)
            {
                _controlTimer.Start();
            }
            else if (!_cursorTimer.IsRunning)
            {
                _cursorTimer.Start();
            }
        }
        else if (e.PropertyName == nameof(ViewModel.CurrentVolume))
        {
            var thumbHeight = VolumeContainer.ActualHeight * (ViewModel.CurrentVolume / ViewModel.MaxVolume);
            VolumeThumb?.Height = Math.Max(0, thumbHeight);
        }
    }

    private void OnControlTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isTouch)
        {
            return;
        }

        _controlTimer.Stop();
        TestRect.Visibility = Visibility.Collapsed;
        ViewModel.Window?.HideCursor();
    }

    private void OnCursorTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsCursorInWindow() && !_isTouch)
        {
            ViewModel.IsControlsVisible = false;
            _cursorTimer.Stop();
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.Close();

    private void OnCloseTipButtonClick(object sender, RoutedEventArgs e)
        => ViewModel.ErrorMessage = default;

    private void OnBackRequested(object sender, EventArgs e)
        => ViewModel.BackCommand.Execute(default);

    private void OnSubtitleSettingItemClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSubtitleSettingsVisible = false;
        ViewModel.IsSubtitleSettingsVisible = true;
    }

    private void OnSubtitlePositionChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.Player?.IsPlaybackInitialized != true || ViewModel.SubtitlePosition == e.NewValue)
        {
            return;
        }

        ViewModel.ChangeSubtitlePositionCommand.Execute(e.NewValue);
    }

    private void OnHeaderBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width == 0)
        {
            return;
        }

        try
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (e.NewSize.Width < 700)
                {
                    VisualStateManager.GoToState(this, nameof(NarrowState), false);
                    HeaderBar.TitleMaxWidth = e.NewSize.Width > 200 ? e.NewSize.Width - 200 : 120;
                }
                else
                {
                    VisualStateManager.GoToState(this, nameof(DefaultState), false);
                    HeaderBar.TitleMaxWidth = e.NewSize.Width - 520;
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnHeaderBarSizeChanged: {ex.Message}");
        }
    }

    private bool IsCursorInWindow()
    {
        var isSuccess = PInvoke.GetCursorPos(out var point);
        if (!isSuccess)
        {
            return false;
        }

        var window = ViewModel.Window?.GetWindow();
        if (window?.IsVisible != true)
        {
            return false;
        }

        var handle = ViewModel.Window!.Handle;
        isSuccess = PInvoke.GetClientRect(new(handle), out var clientRect);
        if (!isSuccess)
        {
            return false;
        }

        var lt = new System.Drawing.Point(clientRect.X, clientRect.Y);
        isSuccess = PInvoke.ClientToScreen(new(handle), ref lt);
        if (!isSuccess)
        {
            return false;
        }

        var rb = new System.Drawing.Point(clientRect.X + clientRect.Width, clientRect.Y + clientRect.Height);
        isSuccess = PInvoke.ClientToScreen(new(handle), ref rb);
        if (!isSuccess)
        {
            return false;
        }

        return point.X >= lt.X && point.X <= rb.X && point.Y >= lt.Y && point.Y <= rb.Y;
    }

    private void OnSubtitleFontSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel?.Player == null || ViewModel.Player?.IsPlaybackInitialized != true || ViewModel.SubtitleFontSize == e.NewValue)
        {
            return;
        }

        ViewModel.ChangeSubtitleFontSizeCommand.Execute(e.NewValue);
    }

#pragma warning disable CA1822
    private string GetTimeText(double seconds)
        => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    private string GetVolumeText(double volume)
        => Math.Round(volume).ToString();

    private string GetSpeedText(double speed)
        => $"{Math.Round(speed, 2)}x";
#pragma warning restore CA1822

    private void OnSubtitleFontChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = e.AddedItems.FirstOrDefault() as SystemFont;
        if (ViewModel?.Player == null || ViewModel.Player?.IsPlaybackInitialized != true || item == null || ViewModel.SubtitleFontFamily == item.LocalName)
        {
            return;
        }

        ViewModel.ChangeSubtitleFontFamilyCommand.Execute(item.LocalName);
    }

    private void OnSubtitleDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        var v = args.NewValue;
        if (v == ViewModel.SubtitleDelaySeconds)
        {
            return;
        }

        ViewModel.SetSubtitleDelaySecondsCommand.Execute(v);
    }

    internal static bool IsPaused(MpvPlayerState state)
    {
        return state is MpvPlayerState.Paused;
    }

    internal static bool IsLoading(MpvPlayerState state, bool isLoading)
    {
        return (state is MpvPlayerState.Seeking or MpvPlayerState.Buffering) || isLoading;
    }

    internal static Visibility IsVolumeIndicatorVisible(bool isControlVisible, bool isVolumeChanging)
    {
        return !isControlVisible && isVolumeChanging ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static Visibility IsBottomProgressShouldDisplay(bool isAot, bool isFullScreen, bool isLoading, MpvPlayerState state)
    {
        if ((isAot || isFullScreen) && !IsLoading(state, isLoading))
        {
            return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    private void OnSourceItemClick(object sender, RoutedEventArgs e)
    {
        var selectedSource = (sender as FrameworkElement)?.DataContext as SourceItemViewModel;
        selectedSource?.ActiveCommand.Execute(default);
        SourceFlyout?.Hide();
    }

    private void OnSubtitleFontComboLoaded(object sender, RoutedEventArgs e)
    {
        var font = ViewModel.SubtitleFontFamily;
        var source = ViewModel.Fonts.FirstOrDefault(p => p.LocalName == font);
        if (source != null)
        {
            SubtitleFontComboBox.SelectedItem = source;
        }
    }

    /// <summary>
    /// 打开顶栏的直播线路菜单：每次打开都按当前流列表重建，切换后文案会跟着变.
    /// </summary>
    /// <param name="sender">菜单.</param>
    /// <param name="e">事件参数.</param>
    private void OnLiveStreamFlyoutOpening(object sender, object e)
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

    /// <summary>
    /// 直播专用的"原画 / 线路"选择器：只在直播时出现在顶栏右侧.
    /// </summary>
    /// <param name="isLive">是否直播.</param>
    /// <returns>可见性.</returns>
    public static Visibility GetLiveSelectorVisibility(bool isLive)
        => isLive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 顶栏右侧那排按钮（画质 / CDN / 空降 / 置顶 / 设置）的可见性：
    /// 播放结束页会占满整屏，这排留着既没用又碍事，只在播放中显示.
    /// </summary>
    /// <param name="isEnded">是否播放结束.</param>
    /// <returns>可见性.</returns>
    public static Visibility GetFooterVisibility(bool isEnded)
        => isEnded ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 控制栏可见性：播放结束时收起（结束页自己带一根不可拖动的进度条）.
    /// </summary>
    /// <param name="controlsVisible">控制栏是否应当可见.</param>
    /// <param name="isEnded">是否播放结束.</param>
    /// <returns>可见性.</returns>
    public static Visibility GetTransportVisibility(bool controlsVisible, bool isEnded)
        => controlsVisible && !isEnded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 结束页可见性：直播不显示（直播没有"播完"）.
    /// </summary>
    /// <param name="isEnded">是否播放结束.</param>
    /// <param name="isLive">是否直播.</param>
    /// <returns>可见性.</returns>
    public static Visibility GetEndPanelVisibility(bool isEnded, bool isLive)
        => isEnded && !isLive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 直播弹幕浮层可见性.
    /// </summary>
    /// <param name="isLive">是否直播.</param>
    /// <param name="isVisible">用户是否打开弹幕.</param>
    /// <returns>可见性.</returns>
    public static Visibility GetLiveDanmakuVisibility(bool isLive, bool isVisible)
        => isLive && isVisible ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>
    /// 空降 / AI 跳过按钮可见性：直播不需要跳广告.
    /// </summary>
    /// <param name="isLive">是否直播.</param>
    /// <returns>可见性.</returns>
    public static Visibility GetAdSkipVisibility(bool isLive)
        => isLive ? Visibility.Collapsed : Visibility.Visible;

    private void OnCloseStatisticsClick(object sender, RoutedEventArgs e)
        => ViewModel?.CloseStatisticsCommand.Execute(default);

    private void OnStatisticsHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingStatistics = true;
        _statisticsDragStart = e.GetCurrentPoint(StatisticsCard).Position;
        _statisticsOffsetStart = new Point(StatisticsTransform.X, StatisticsTransform.Y);
        StatisticsHeader.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnStatisticsHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingStatistics)
        {
            return;
        }

        var current = e.GetCurrentPoint(StatisticsCard).Position;
        var targetX = _statisticsOffsetStart.X + (current.X - _statisticsDragStart.X);
        var targetY = _statisticsOffsetStart.Y + (current.Y - _statisticsDragStart.Y);

        // 限制在播放器可视范围内，别拖出画面找不回来.
        var maxX = Math.Max(0, ActualWidth - StatisticsCard.ActualWidth - 48);
        var maxY = Math.Max(0, ActualHeight - StatisticsCard.ActualHeight - 48);
        StatisticsTransform.X = Math.Clamp(targetX, 0, maxX);
        StatisticsTransform.Y = Math.Clamp(targetY, 0, maxY);
        e.Handled = true;
    }

    private void OnStatisticsHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingStatistics)
        {
            return;
        }

        _isDraggingStatistics = false;
        StatisticsHeader.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }
}









