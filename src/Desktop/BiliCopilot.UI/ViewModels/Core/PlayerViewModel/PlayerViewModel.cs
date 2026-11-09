// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Richasy.WinUIKernel.Share.ViewModels;
using WinUIEx;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型.
/// </summary>
public sealed partial class PlayerViewModel(DispatcherQueue queue, ILogger<PlayerViewModel> logger) : ViewModelBase, IAsyncDisposable
{
    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Danmaku.ClearAll();
        _nativeMp?.Dispose();
        _nativeMp = null;

        if (_rightKeyLongPressTimer != null)
        {
            _rightKeyLongPressTimer.Tick -= OnRightKeyLongPressTimerTick;
            _rightKeyLongPressTimer.Stop();
            _rightKeyLongPressTimer = null;
        }

        if (_tripleTimer != null)
        {
            _tripleTimer.Tick -= OnTripleTimerTick;
            _tripleTimer.Stop();
            _tripleTimer = null;
        }

        if (_smtc != null)
        {
            _smtc.ButtonPressed -= OnSmtcButtonPressed;
            _smtc = null;
        }

        if (Connector != null)
        {
            Connector.PlaylistInitialized -= OnConnectorPlaylistInitialized;
            Connector.NewMediaRequest -= OnConnectorNewMediaRequest;
            Connector.RequestOpenExtraPanel -= OnRequestOpenExtraPanel;
            Connector.PropertiesUpdated -= OnConnectorPropertiesUpdated;
        }

        if (Client?.IsDisposed == false)
        {
            Client.ErrorOccurred -= OnClientErrorOccurred;
            Client.CacheStateChanged -= OnClientCacheStateChanged;
            await Client.DisposeAsync();
        }

        if (Player is not null)
        {
            Player.PropertyChanged -= OnPlayerPropertyChanged;
            await Player.DisposeAsync();
        }

        if (Window?.IsDisposed == false)
        {
            Window.SizeChanged -= OnWindowSizeChanged;
            Window.GetWindow().Destroying -= OnWindowDestroying;
            await Window.DisposeAsync();
        }

        this.Get<AppViewModel>().Players.Remove(this);
    }

    [RelayCommand]
    internal Task InitializeAsync(MediaSnapshot snapshot)
        => InitializeCoreAsync(snapshot, reusePlayer: CanReusePlayer());

    /// <summary>
    /// 从结束页切到另一个视频：复用现有 mpv 实例，只换解析器再重新加载媒体.
    /// 重建实例正是白屏卡死的根源；重播按钮稳，也是因为它全程没碰播放器实例.
    /// </summary>
    /// <param name="snapshot">新视频快照.</param>
    /// <returns>任务.</returns>
    internal Task InitializeWithReuseAsync(MediaSnapshot snapshot)
        => InitializeCoreAsync(snapshot, reusePlayer: true);

    /// <summary>
    /// 已经有可用 mpv 实例时，切视频一律复用，不重建.
    /// 播放列表 / 合集 / 分 P / 结束页推荐全走这条判断：重建实例会白屏卡死.
    /// </summary>
    /// <returns>能否复用.</returns>
    private bool CanReusePlayer()
        => !UseWebEngine
        && !_forceWebFallback
        && !_mpvInitFailed
        && Player is { IsWebEngine: false, Mpv: not null };

    private async Task InitializeCoreAsync(MediaSnapshot snapshot, bool reusePlayer)
    {
        IsLive = snapshot.Type == BiliMediaType.Live;
        _snapshot = snapshot;
        HookCdnStatus();
        ResetEndScreen();
        _sponsorSegments = Array.Empty<SponsorSegment>();
        _lastSponsorSkipPosition = -1;

        // 注意：空降检查不能在这里启动——那时 _sourceResolver 还是 null，
        // 音频识别会直接以"没有播放源"跳过（AI 声音跳过永远跑不起来）。
        // 挪到下面解析器创建完之后再启动.
        _watchTracker = new PlaybackWatchTracker();
        _watchRecorded = false;
        ErrorMessage = default;
        CacheSpeedText = default;
        ShowPlayerWindow();
        IsConnecting = true;
        StartConnectWatchdog();
        IsSourceSelectable = false;
        IsDanmakuEnabled = false;
        IsDanmakuLoading = false;
        IsStatsOverlayShown = false;
        IsEnd = false;
        _isTlsFailed = false;
        _prevPosition = 0;
        IsSubtitleEmpty = true;
        _isDanmakuInitialized = false;
        IsNextTipShownInThisMedia = false;
        Danmaku.ClearAll();
        Chapters.Clear();
        IsChapterVisible = SettingsToolkit.ReadLocalSetting(SettingNames.IsChapterVisible, true);
        UseIntegrationOperation = SettingsToolkit.ReadLocalSetting(SettingNames.HideMainWindowOnPlay, true)
            && SettingsToolkit.ReadLocalSetting(SettingNames.UseIntegrationWhenSinglePlayWindow, true);
        BackwardTip = string.Format(ResourceToolkit.GetLocalizedString(StringNames.BackwardTemplate), SettingsToolkit.ReadLocalSetting(SettingNames.StepBackwardSecond, 10d));
        ForwardTip = string.Format(ResourceToolkit.GetLocalizedString(StringNames.ForwardTemplate), SettingsToolkit.ReadLocalSetting(SettingNames.StepForwardSecond, 30d));
        RequestHideNextTip?.Invoke(this, EventArgs.Empty);
        if (Client != null)
        {
            try
            {
                await Client.PauseAsync();
                await Client.StopAsync();
            }
            catch
            {
            }
        }

        try
        {
            await InitializeInternalAsync();
        }
        catch (Exception ex)
        {
            // mpv 初始化失败（缺 dll、驱动异常等）时记账，等解析器就绪后改走网页播放器.
            logger.LogWarning(ex, "mpv 初始化失败，本次播放回落到网页播放器");
            _mpvInitFailed = true;
        }
        if (Connector != null)
        {
            System.Diagnostics.Debug.WriteLine($"Player initialized with connector: {Connector.GetType().Name}");
            await Connector.InitializeAsync(_snapshot, default);
            logger.LogInformation("初始化：连接器就绪");
            if (Window.IsClosed)
            {
                return;
            }
        }

        Title = Connector switch
        {
            VideoConnectorViewModel videoVM => videoVM.Title,
            PgcConnectorViewModel pgcVM => pgcVM.Title,
            _ => _snapshot.Live?.Identifier.Title ?? string.Empty
        };

        // TODO: 创建源处理器
        switch (_snapshot.Type)
        {
            case BiliMediaType.Video:
                {
                    var resolver = this.Get<VideoMediaSourceResolver>();
                    var connector = Connector as VideoConnectorViewModel;
                    resolver.Initialize(_snapshot, connector._part);
                    _sourceResolver = resolver;
                }
                break;
            case BiliMediaType.Pgc:
                {
                    var resolver = this.Get<PgcMediaSourceResolver>();
                    var connector = Connector as PgcConnectorViewModel;
                    resolver.Initialize(_snapshot, connector._episode);
                    _sourceResolver = resolver;
                }
                break;
            case BiliMediaType.Live:
                {
                    var resolver = this.Get<LiveMediaSourceResolver>();
                    resolver.Initialize(_snapshot.Live!);
                    _sourceResolver = resolver;
                    _ = ConnectLiveAsync(default);
                }

                break;
            default:
                break;
        }

        _sourceResolver.WindowHandle = Window!.Handle;

        // 解析器就绪后再跑空降检查：音频识别要用它取音轨地址.
        if (snapshot.Type == BiliMediaType.Video)
        {
            _ = LoadSponsorBlockAsync(snapshot);
        }

        if (_historyResolver != null && Player != null)
        {
            try
            {
                // 上一条的历史保存是"锦上添花"，不能让它把新视频的初始化卡住.
                await _historyResolver.SaveHistoryAsync(Player.Position, Player.Duration, Richasy.MpvKernel.Core.Enums.MpvPlayerState.End, true)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "保存上一条播放历史失败或超时，继续初始化");
            }
        }

        // TODO: 创建历史记录处理器
        switch (_snapshot.Type)
        {
            case BiliMediaType.Video:
                {
                    var resolver = this.Get<VideoMediaHistoryResolver>();
                    var connector = Connector as VideoConnectorViewModel;
                    resolver.Initialize(connector._view, connector._part, _snapshot);
                    _historyResolver = resolver;
                }
                break;
            case BiliMediaType.Pgc:
                {
                    var resolver = this.Get<PgcMediaHistoryResolver>();
                    var connector = Connector as PgcConnectorViewModel;
                    resolver.Initialize(connector._view, connector._episode, _snapshot);
                    _historyResolver = resolver;
                }
                break;
            case BiliMediaType.Live:
                break;
            default:
                break;
        }

        if (Window.IsClosed)
        {
            return;
        }

        if (UseWebEngine || _forceWebFallback || _mpvInitFailed)
        {
            _forceWebFallback |= _mpvInitFailed;
            await InitializeWebEngineAsync();
            logger.LogInformation("本次播放使用网页播放器（Shaka）");
        }
        else if (reusePlayer && Player is { IsWebEngine: false, Mpv: not null })
        {
            // 收尾时换视频（结束页点推荐）走这条路：不重建 mpv，只把解析器换成新视频的，
            // 后面再让现有播放器重新加载媒体。重建实例就是白屏卡死的根源，重播按钮能稳
            // 也是因为它全程没碰播放器实例.
            logger.LogInformation("初始化：复用现有 mpv（不重建实例）");
            Player.Mpv.UpdateResolvers(_sourceResolver, _historyResolver);
        }
        else
        {
            // 重载时必须先把上一个播放器放掉：MpvPlayer 共用同一个 mpv client，
            // 旧实例留着会和新实例抢同一路视频输出，表现就是"有声音、画面不动".
            if (Player is not null)
            {
                Player.PropertyChanged -= OnPlayerPropertyChanged;
                await Player.DisposeAsync();
                Player = null;
            }

            logger.LogInformation("初始化：创建播放器");
            Player = new PlayerSurface(new Richasy.MpvKernel.Player.MpvPlayer(
                Client!,
                _sourceResolver,
                _historyResolver,
                logger: new MpvLogWatcher(logger, OnMpvSuspectLog)));
            Player.PropertyChanged += OnPlayerPropertyChanged;
            Player.Mpv!.UpdateResolvers(_sourceResolver, _historyResolver);

            try
            {
                // mpv 建实例偶尔会卡死（同一窗口里连着换视频时实测过，45 秒都不返回，
                // 界面就一直白屏+连接中）。给个上限，超时就走网页播放器，别让用户干等.
                await Player.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(20));
                logger.LogInformation("初始化：播放器就绪");
            }
            catch (TimeoutException)
            {
                logger.LogWarning("mpv 播放器初始化超过 20 秒没返回，改用网页播放器");
                var stuck = Player;
                Player = null;
                if (!await TryFallbackToWebEngineAsync())
                {
                    ErrorMessage = "播放器初始化超时，请重试";
                    _ = DisposeStuckPlayerAsync(stuck);
                    return;
                }

                logger.LogInformation("已回落到网页播放器（mpv 初始化超时）");
                _ = DisposeStuckPlayerAsync(stuck);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "mpv 播放器初始化失败，尝试回落到网页播放器");
                if (!await TryFallbackToWebEngineAsync())
                {
                    ErrorMessage = ex.Message;
                    return;
                }
            }
        }

        if (Window.IsClosed)
        {
            return;
        }

        logger.LogInformation("初始化：重置呈现模式");
        await ResetPlayerPresenterModeAsync();
        if (Window.IsClosed)
        {
            return;
        }

        CheckFullScreen();
        CheckCompactOverlay();
        ProgressChanged?.Invoke(this, 0);
        try
        {
            logger.LogInformation("重载诊断：开始解析播放源");
            await LoadSourcesAsync();
            logger.LogInformation("重载诊断：播放源解析完成，开始加载字幕");
            await LoadSubtitlesAsync();
            logger.LogInformation("重载诊断：字幕加载完成");
            if (Window.IsClosed)
            {
                return;
            }

            if (reusePlayer && Player is { IsWebEngine: false })
            {
                // 复用路径：解析器已经指向新视频，让现有播放器按新源重新加载即可.
                logger.LogInformation("初始化：复用播放器重新加载媒体");
                await Player.ReplayAsync();
            }

            if (UseWebEngine)
            {
                await LoadWebMediaAsync();
            }
        }
        catch (Exception ex)
        {
            // 必须兜住：以前没有 try/finally，任何异常都会让 IsConnecting 永远为 true，
            // 界面就卡在"连接中，请稍候"——重播走重载链路时实测就是这样卡死的.
            logger.LogError(ex, "加载播放源或字幕失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            StopConnectWatchdog();
            logger.LogInformation("重载诊断：播放初始化收尾，关闭连接中状态");
            IsConnecting = false;
        }
    }

    /// <summary>
    /// 初始化看门狗：45 秒还没收尾就把"连接中"关掉，免得界面永远卡住.
    /// 正常初始化结束时由 StopConnectWatchdog 取消.
    /// </summary>
    private void StartConnectWatchdog()
    {
        StopConnectWatchdog();
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(45);
        timer.IsRepeating = false;
        timer.Tick += OnConnectWatchdogTick;
        _connectWatchdog = timer;
        timer.Start();
    }

    private void StopConnectWatchdog()
    {
        if (_connectWatchdog is null)
        {
            return;
        }

        _connectWatchdog.Tick -= OnConnectWatchdogTick;
        _connectWatchdog.Stop();
        _connectWatchdog = null;
    }

    /// <summary>
    /// mpv 报了"设备丢失 / swapchain 建不出来"这类消息：画面会直接变黑或白，
    /// 但音频还在跑，用户只能重开。这里自动重建一次播放器，还不行就退到网页播放器.
    /// </summary>
    /// <param name="message">触发恢复的 mpv 日志原文.</param>
    private void OnMpvSuspectLog(string message)
    {
        if (_isDeviceRecovering)
        {
            return;
        }

        _isDeviceRecovering = true;
        queue.TryEnqueue(() => _ = RecoverFromDeviceLostAsync(message));
    }

    private async Task RecoverFromDeviceLostAsync(string message)
    {
        try
        {
            _deviceRecoverAttempts++;
            logger.LogWarning("检测到画面输出异常（第 {Count} 次）：{Message}", _deviceRecoverAttempts, message);

            if (_deviceRecoverAttempts == 1)
            {
                ShowPlayerTip("画面输出异常，正在重建播放器…", true);
                var position = Player?.Position ?? 0;
                await ReloadAtAsync(position);
                return;
            }

            // 重建过一次还是丢设备，说明这台机器的 mpv 输出已经不能用了，直接换网页播放器.
            ShowPlayerTip("画面仍未恢复，已切换到网页播放器", true);
            if (!await TryFallbackToWebEngineAsync(Player?.Position ?? 0))
            {
                ShowPlayerTip("播放器输出异常，请重开播放窗口", true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "重建播放器失败");
            ShowPlayerTip("播放器输出异常，请重开播放窗口", true);
        }
        finally
        {
            _isDeviceRecovering = false;
        }
    }

    /// <summary>
    /// 放掉那个卡住的 mpv 播放器（不给它无限期拖住初始化线程）.
    /// </summary>
    /// <param name="player">卡住的播放器实例.</param>
    private async Task DisposeStuckPlayerAsync(PlayerSurface? player)
    {
        if (player is null)
        {
            return;
        }

        try
        {
            player.PropertyChanged -= OnPlayerPropertyChanged;
            await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "释放卡住的播放器失败，忽略");
        }
    }

    private void OnConnectWatchdogTick(DispatcherQueueTimer sender, object args)
    {
        sender.Tick -= OnConnectWatchdogTick;
        _connectWatchdog = null;
        if (!IsConnecting)
        {
            return;
        }

        logger.LogWarning("初始化超过 45 秒仍未收尾，强制关闭连接中状态");
        IsConnecting = false;
    }

    public void UpdateTheme(ElementTheme theme)
        => Window?.SetTheme(theme);

    /// <summary>
    /// 关闭播放器.
    /// </summary>
    public void Close()
    {
        StopLive();
        Window?.Close();
    }

    private void ShowPlayerWindow()
    {
        if (Window?.IsDisposed != false)
        {
            Window = new PlayerWindow();
            Window.SetActiveAction(isActive => queue.TryEnqueue(DispatcherQueuePriority.Low, () => IsWindowDeactivated = !isActive));
            Window.GetWindow().Destroying += OnWindowDestroying;
            Window.SizeChanged += OnWindowSizeChanged;
            var pointerSource = InputPointerSource.GetForIsland(Window.XamlRoot?.ContentIsland);
            pointerSource.PointerReleased += OnWindowPointerReleased;
            var keyboardSource = InputKeyboardSource.GetForIsland(Window.XamlRoot?.ContentIsland);
            keyboardSource.KeyDown += OnWindowKeyDown;
            keyboardSource.KeyUp += OnWindowKeyUp;
        }

        var hideMainWindowOnPlay = SettingsToolkit.ReadLocalSetting(SettingNames.HideMainWindowOnPlay, true);

        if (!Window.GetWindow().IsVisible)
        {
            Window!.Show(Player == null && !hideMainWindowOnPlay);
        }

        var isFirstLoad = !_uiElementSetted;
        if (!_uiElementSetted)
        {
            var overlay = new PlayerOverlay { ViewModel = this };
            Window!.SetUIElement(overlay);
            _uiElementSetted = true;

            var currentTheme = SettingsToolkit.ReadLocalSetting(SettingNames.AppTheme, ElementTheme.Default);
            Window.SetTheme(currentTheme);
        }

        if (hideMainWindowOnPlay && isFirstLoad)
        {
            var mainWnd = this.Get<Core.AppViewModel>().Windows.First(p => p is MainWindow);
            if (mainWnd.AppWindow.IsVisible)
            {
                var scaleFactor = HwndExtensions.GetDpiForWindow(mainWnd.GetWindowHandle()) / 96d;
                var windowLeft = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowPositionLeft, 0);
                var windowTop = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowPositionTop, 0);
                var windowWidth = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowWidth, 1120d);
                var windowHeight = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowHeight, 740d);
                Window.GetWindow().MoveAndResize(new Windows.Graphics.RectInt32(windowLeft, windowTop, Convert.ToInt32(windowWidth * scaleFactor), Convert.ToInt32(windowHeight * scaleFactor)));
                if (SettingsToolkit.ReadLocalSetting(SettingNames.IsMainWindowMaximized, false))
                {
                    (Window.GetWindow().Presenter as OverlappedPresenter)?.Maximize();
                }

                mainWnd.Hide();
            }
        }

        if (isFirstLoad)
        {
            var defaultPresenter = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerPresenterMode, PlayerPresenterMode.Default);
            if (defaultPresenter == PlayerPresenterMode.FullScreen)
            {
                Window.EnterFullScreen();
            }
            else if (defaultPresenter == PlayerPresenterMode.CompactOverlay)
            {
                Window.EnterCompactOverlay();
            }
        }

        IsVerticalScreen = Window!.GetWindow().Presenter.Kind is Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen or Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay
            ? Window.GetWindow().Size.Height > Window.GetWindow().Size.Width
            : Window.GetWindow().Size.Height >= Window.GetWindow().Size.Width;
        CheckBlackBackgroundVisible();
    }

    private async Task SelectSourceAsync(SourceItemViewModel source)
    {
        if (source is null)
        {
            return;
        }

        var previous = Sources.FirstOrDefault(p => p.IsSelected);
        foreach (var item in Sources)
        {
            item.IsSelected = item.Equals(source);
        }

        var selected = Sources.FirstOrDefault(s => s.IsSelected);
        SelectedSource = selected;

        // 原实现只是 Player.ReplayAsync(Player.Position)：那是把"已经加载的旧媒体"原地重播一遍，
        // 解析器没有重跑、新清晰度的地址根本没取，所以切了等于没切；再加上重播后视频输出没接回去，
        // 就成了"画面静止、只有声音".
        // 现在和结束页切视频走同一条路：复用现有 mpv（不 dispose 再 new，重建正是白屏卡死/闪退的根源），
        // 换掉解析器的清晰度后让播放器按新源重新加载，再跳回原位置.
        if (selected is null || _sourceResolver is not VideoMediaSourceResolver videoResolver)
        {
            return;
        }

        _continuePosition = Player?.Position;
        _snapshot.PreferQuality = selected.Data.Quality;
        videoResolver.ResetSnapshot(_snapshot);

        try
        {
            await InitializeWithReuseAsync(_snapshot);

            // InitializeCoreAsync 内部把异常兜住了，失败只会写 ErrorMessage，所以这里看它.
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                throw new InvalidOperationException(ErrorMessage);
            }

            var target = _continuePosition ?? 0;
            if (Player is { IsWebEngine: false } && target > 0.5)
            {
                var duration = Player.Duration;
                if (duration > 1)
                {
                    target = Math.Min(target, duration - 1);
                }

                await ApplySeekAsync(target);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "切换清晰度失败：{Quality}", selected.Data.Quality);
            ShowPlayerTip($"切换到 {selected.Data.Quality} 失败，已退回原来的清晰度", true);

            // 回滚选择状态，别让界面显示成"已经切过去了".
            if (previous is not null)
            {
                foreach (var item in Sources)
                {
                    item.IsSelected = item.Equals(previous);
                }

                SelectedSource = previous;
                _snapshot.PreferQuality = previous.Data.Quality;
                videoResolver.ResetSnapshot(_snapshot);
            }
        }
    }

    private void CheckBottomProgressBarVisible()
        => IsBottomProgressBarVisible = !IsLive
            && !IsControlsVisible
            && SettingsToolkit.ReadLocalSetting(SettingNames.IsBottomProgressBarVisible, true);

    partial void OnIsControlsVisibleChanged(bool value)
        => Window?.SetCaptionButtonVisibility(value);

    partial void OnIsControlsVisibleChanged(bool oldValue, bool newValue)
    {
        IsTopMost = Window?.IsTopMost() ?? false;
        CheckBottomProgressBarVisible();
    }

    partial void OnIsSubtitleEnabledChanged(bool value)
    {
        ToggleSubtitleEnabledCommand.Execute(value);
    }

    private async Task LoadSponsorBlockAsync(MediaSnapshot snapshot)
    {
        if (!SettingsToolkit.ReadLocalSetting(SettingNames.EnableSponsorBlock, true))
        {
            return;
        }

        var rawId = snapshot.Type == BiliMediaType.Video ? snapshot.Video?.Identifier.Id : null;
        if (string.IsNullOrEmpty(rawId) || _isSponsorLoading)
        {
            return;
        }

        // 快照里给的是 aid（114929466089407），SponsorBlock 要的是 BV 号，先换一下.
        var videoId = await SponsorBlockService.ResolveBvidAsync(rawId, logger);
        if (string.IsNullOrEmpty(videoId))
        {
            logger.LogWarning("拿不到 BV 号，空降助手跳过本次查询");
            return;
        }

        _isSponsorLoading = true;
        try
        {
            // 检查过程写进左下角药丸：先报下在查哪个视频、几路来源.
            var sourceNames = new List<string> { "SponsorBlock" };
            if (AdCloudService.IsEnabled)
            {
                sourceNames.Add("云端库");
            }

            if (BiliJumpService.IsEnabled)
            {
                sourceNames.Add("Bilijump");
            }

            if (AiAdAnalyzer.IsEnabled)
            {
                sourceNames.Add("AI");
            }

            PushAdSkipLog($"空降：检查 {videoId}（{string.Join(" + ", sourceNames)}）", TimeSpan.FromSeconds(12));
            logger.LogInformation(
                "空降助手设置：开关={Enabled} 手动={Manual} 最短={Min:F0}s 服务器={Server} AI={Ai} 音频={Audio} 云端={Cloud}",
                SettingsToolkit.ReadLocalSetting(SettingNames.EnableSponsorBlock, true),
                SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockManualMode, false),
                SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockMinDuration, 3d),
                SponsorBlockService.CurrentServer,
                AiAdAnalyzer.IsEnabled,
                AiAdAnalyzer.IsAudioEnabled,
                AdCloudService.IsEnabled);

            // 四路来源并行查：SponsorBlock（可换镜像）、云端广告库、Bilijump 数据库、AI 识别.
            // 并行是为了不让最慢的一路（AI）拖住其它来源，谁先回来先并入.
            var segments = new List<SponsorSegment>();
            var sponsorTask = SponsorBlockService.GetSegmentsAsync(videoId, logger);
            var cloudTask = AdCloudService.IsEnabled
                ? AdCloudService.GetSegmentsAsync(videoId, logger)
                : Task.FromResult<IReadOnlyList<SponsorSegment>>([]);
            var bilijumpTask = BiliJumpService.IsEnabled
                ? QueryBiliJumpAsync(videoId)
                : Task.FromResult<IReadOnlyList<SponsorSegment>>([]);
            var aiTask = AiAdAnalyzer.IsEnabled
                ? AnalyzeAdsWithAiAsync(videoId, snapshot)
                : Task.FromResult<IReadOnlyList<SponsorSegment>>([]);

            await Task.WhenAll(sponsorTask, cloudTask, bilijumpTask, aiTask);

            var sponsorSegments = await sponsorTask;
            segments.AddRange(sponsorSegments);
            logger.LogInformation("片段来源 sponsorblock：{Count} 段（视频 {VideoId}，服务器 {Server}）", sponsorSegments.Count, videoId, SponsorBlockService.CurrentServer);
            PushAdSkipLog($"来源 SponsorBlock（{SponsorBlockService.CurrentServer}）：{sponsorSegments.Count} 段", TimeSpan.FromSeconds(10));
            foreach (var item in sponsorSegments)
            {
                logger.LogInformation("  片段 {Start:F1}-{End:F1}s {Category}({Action})", item.Start, item.End, item.CategoryName, item.ActionType);
            }

            if (AdCloudService.IsEnabled)
            {
                var cloudSegments = await cloudTask;
                segments.AddRange(cloudSegments);
                logger.LogInformation("片段来源 云端广告库：{Count} 段", cloudSegments.Count);
                PushAdSkipLog($"来源 云端广告库：{cloudSegments.Count} 段", TimeSpan.FromSeconds(10));
            }
            else
            {
                logger.LogInformation("云端广告库未启用，跳过该来源");
                PushAdSkipLog("来源 云端广告库：未启用，跳过", TimeSpan.FromSeconds(10));
            }

            if (BiliJumpService.IsEnabled)
            {
                var bilijumpSegments = await bilijumpTask;
                segments.AddRange(bilijumpSegments);
                logger.LogInformation("片段来源 Bilijump 云端库：{Count} 段", bilijumpSegments.Count);
                PushAdSkipLog($"来源 Bilijump 云端库：{bilijumpSegments.Count} 段", TimeSpan.FromSeconds(10));
                foreach (var item in bilijumpSegments)
                {
                    logger.LogInformation("  Bilijump {Start:F1}-{End:F1}s 广告主={Note}", item.Start, item.End, string.IsNullOrEmpty(item.Note) ? "未标注" : item.Note);
                }
            }
            else
            {
                logger.LogInformation("Bilijump 云端库未启用，跳过该来源");
                PushAdSkipLog("来源 Bilijump：未启用，跳过", TimeSpan.FromSeconds(10));
            }

            if (AiAdAnalyzer.IsEnabled)
            {
                var aiSegments = await aiTask;
                segments.AddRange(aiSegments);
                logger.LogInformation("片段来源 AI 识别：{Count} 段（音频分析 {Audio}）", aiSegments.Count, AiAdAnalyzer.IsAudioEnabled ? "开" : "关");
                PushAdSkipLog($"来源 AI 识别：{aiSegments.Count} 段（模型 {AiAdAnalyzer.CurrentSubtitleModel}）", TimeSpan.FromSeconds(10));
            }
            else
            {
                logger.LogInformation("AI 识别未启用，跳过该来源");
                PushAdSkipLog("来源 AI 识别：未启用，跳过", TimeSpan.FromSeconds(10));
            }

            var testRanges = ReadSegmentRangeTest();
            _sponsorSegments = testRanges.Count > 0 ? testRanges : MergeSegments(segments);
            logger.LogInformation("跳过片段检查完成：{Count} 段", _sponsorSegments.Count);
            PushAdSkipLog($"检查完成：共 {_sponsorSegments.Count} 段", TimeSpan.FromSeconds(8));
            if (_sponsorSegments.Count > 0)
            {
                logger.LogInformation("共找到 {Count} 段可跳过片段（来源：{Sources}）", _sponsorSegments.Count,
                    string.Join("/", _sponsorSegments.Select(p => p.Source).Distinct()));
                var stamps = string.Join("、", _sponsorSegments.Take(3).Select(p => $"{FormatSkipStamp(p.Start)}–{FormatSkipStamp(p.End)}"));
                var more = _sponsorSegments.Count > 3 ? $"…共 {_sponsorSegments.Count} 段" : string.Empty;
                PushAdSkipLog($"监测到广告片段 {stamps}{more}");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "获取跳过片段失败");
            _sponsorSegments = Array.Empty<SponsorSegment>();
        }
        finally
        {
            _isSponsorLoading = false;
        }
    }

    /// <summary>
    /// 查 Bilijump 云端广告库（按 cid），并行参与上面的多源合并.
    /// </summary>
    private async Task<IReadOnlyList<SponsorSegment>> QueryBiliJumpAsync(string videoId)
    {
        var cid = await BiliJumpService.ResolveCidAsync(videoId, logger);
        if (string.IsNullOrEmpty(cid))
        {
            logger.LogInformation("Bilijump：拿不到 cid，跳过");
            return [];
        }

        var result = await BiliJumpService.GetSegmentsAsync(cid, logger);
        logger.LogInformation("Bilijump：cid={Cid} 命中 {Count} 段", cid, result.Count);
        return result;
    }

    /// <summary>
    /// 更新药丸提示文字：剩余时间 + 来源 + 广告主.
    /// </summary>
    private void UpdateSkipPromptCountdown(SponsorSegment segment, double position)
    {
        var remaining = Math.Max(0, (int)Math.Ceiling(segment.End - position));
        var source = DescribeSegmentSource(segment.Source);
        var text = string.IsNullOrEmpty(segment.Note)
            ? $"跳过广告片段 · 剩 {remaining}s · {source}"
            : $"跳过广告 · {segment.Note} · 剩 {remaining}s · {source}";
        if (!string.Equals(SkipPromptText, text, StringComparison.Ordinal))
        {
            SkipPromptText = text;
        }
    }

    private static string DescribeSegmentSource(string? source)
        => (source ?? string.Empty).ToLowerInvariant() switch
        {
            "sponsorblock" => "空降助手",
            "cloud" => "云端库",
            "bilijump" => "Bilijump 库",
            "ai" => "AI 识别",
            "test" => "自检",
            _ => "未知来源",
        };

    private async Task<IReadOnlyList<SponsorSegment>> AnalyzeAdsWithAiAsync(string videoId, MediaSnapshot snapshot)
    {
        var stageLife = TimeSpan.FromSeconds(12);

        // 自检：LocalState 下放 adskip-ai-test.txt，内容当作字幕直接喂给 AI，用来验证识别流程.
        var subtitleText = ReadAiTestSubtitle() ?? await GetSubtitleTextAsync(snapshot);
        if (!string.IsNullOrWhiteSpace(subtitleText))
        {
            logger.LogInformation("字幕识别素材：{Length} 字", subtitleText.Length);
            PushAdSkipLog(
                $"字幕素材：{subtitleText.Length} 字 → {AiAdAnalyzer.CurrentSubtitleModel}",
                stageLife);
            var result = await AiAdAnalyzer.AnalyzeSubtitleAsync(
                subtitleText,
                logger,
                text => PushAdSkipLog(text, stageLife));
            logger.LogInformation("AI 字幕识别结果：{Count} 段", result.Count);
            if (result.Count > 0)
            {
                return result;
            }
        }
        else
        {
            logger.LogInformation("没有可用字幕，准备走音频识别");
            PushAdSkipLog("字幕素材：没有字幕，改走音频识别", stageLife);
        }

        if (!AiAdAnalyzer.IsAudioEnabled || _sourceResolver is null)
        {
            logger.LogInformation("音频识别未启用或没有播放源，跳过");
            PushAdSkipLog("音频识别：未启用或没有播放源，跳过", TimeSpan.FromSeconds(10));
            return Array.Empty<SponsorSegment>();
        }

        if (!AiAdAnalyzer.HasDashScopeKey)
        {
            logger.LogInformation("未配置阿里云 DashScope Key，音频识别跳过（无字幕时 AI 跳过不可用）");
            PushAdSkipLog("音频识别：没配 DashScope Key，跳过", TimeSpan.FromSeconds(10));
            return Array.Empty<SponsorSegment>();
        }

        try
        {
            var source = await _sourceResolver.GetSourceAsync();
            var audioUrl = source?.Options?.AudioTracks?.FirstOrDefault();
            if (string.IsNullOrEmpty(audioUrl))
            {
                logger.LogInformation("没有音轨地址，音频识别跳过");
                PushAdSkipLog("音频识别：拿不到音轨地址，跳过", TimeSpan.FromSeconds(10));
                return Array.Empty<SponsorSegment>();
            }

            var cookie = this.Get<Richasy.BiliKernel.Bili.Authorization.IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;
            PushAdSkipLog($"音频识别：{AiAdAnalyzer.CurrentAudioModel} 转写 → 大模型", TimeSpan.FromSeconds(12));
            return await AiAdAnalyzer.AnalyzeAudioAsync(
                audioUrl,
                cookie,
                logger,
                text => PushAdSkipLog(text, TimeSpan.FromSeconds(12)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "音频广告识别失败");
            PushAdSkipLog($"音频识别失败：{ex.Message}", TimeSpan.FromSeconds(10));
            return Array.Empty<SponsorSegment>();
        }
    }

    private async Task<string?> GetSubtitleTextAsync(MediaSnapshot snapshot)
    {
        try
        {
            if (Connector is not VideoConnectorViewModel videoVM)
            {
                return null;
            }

            var aid = videoVM._view.Information.Identifier.Id;
            var cid = videoVM._part.Identifier.Id;
            var service = this.Get<Richasy.BiliKernel.Bili.Media.ISubtitleService>();
            var metas = await service.GetSubtitleMetasAsync(aid, cid);
            if (metas is null || metas.Count == 0)
            {
                logger.LogInformation("BiliKernel 没有字幕元数据，改走 player/v2 接口");
                return await GetSubtitleTextFromApiAsync(aid, cid);
            }

            var meta = metas.FirstOrDefault(p => !p.IsAI) ?? metas[0];
            var url = meta.Url?.ToString();
            if (string.IsNullOrEmpty(url))
            {
                logger.LogInformation("字幕元数据没有地址，改走 player/v2 接口");
                return await GetSubtitleTextFromApiAsync(aid, cid);
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var content = await client.GetStringAsync(url);
            using var document = System.Text.Json.JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("body", out var body) || body.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var item in body.EnumerateArray())
            {
                var from = item.TryGetProperty("from", out var fromNode) ? fromNode.GetDouble() : 0;
                var to = item.TryGetProperty("to", out var toNode) ? toNode.GetDouble() : 0;
                var text = item.TryGetProperty("content", out var textNode) ? textNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                builder.AppendLine($"{from:F2} --> {to:F2}");
                builder.AppendLine(text);
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "获取字幕文本失败");
            return null;
        }
    }

    /// <summary>
    /// 备用字幕通道：直接打 B 站 player/v2 接口拿字幕地址（对齐 bilijump-ai 的做法）.
    /// BiliKernel 的 ISubtitleService 拿不到时用这条路.
    /// </summary>
    private async Task<string?> GetSubtitleTextFromApiAsync(string aid, string cid)
    {
        try
        {
            var cookie = this.Get<Richasy.BiliKernel.Bili.Authorization.IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            if (!string.IsNullOrEmpty(cookie))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
            }

            var metaJson = await client.GetStringAsync($"https://api.bilibili.com/x/player/v2?aid={aid}&cid={cid}");
            using var metaDocument = System.Text.Json.JsonDocument.Parse(metaJson);
            if (!metaDocument.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("subtitle", out var subtitle) ||
                !subtitle.TryGetProperty("subtitles", out var subtitles) ||
                subtitles.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            var candidates = new List<(string Url, double Weight)>();
            foreach (var item in subtitles.EnumerateArray())
            {
                var url = item.TryGetProperty("subtitle_url", out var urlNode) ? urlNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (url.StartsWith("//", StringComparison.Ordinal))
                {
                    url = "https:" + url;
                }

                var weight = item.TryGetProperty("weight", out var weightNode) && weightNode.TryGetDouble(out var w) ? w : 0;
                candidates.Add((Url: url, Weight: weight));
            }

            var content = await FetchSubtitleBodyAsync(client, candidates);
            if (content is null)
            {
                logger.LogInformation("字幕接口有 {Count} 条字幕地址，但都没取到内容", candidates.Count);
                return null;
            }

            return content;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "备用字幕接口失败");
            return null;
        }
    }

    private async Task<string?> FetchSubtitleBodyAsync(HttpClient client, IReadOnlyList<(string Url, double Weight)> candidates)
    {
        foreach (var candidate in candidates.OrderByDescending(p => p.Weight))
        {
            try
            {
                var json = await client.GetStringAsync(candidate.Url);
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("body", out var body) || body.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    continue;
                }

                var builder = new System.Text.StringBuilder();
                foreach (var item in body.EnumerateArray())
                {
                    var from = item.TryGetProperty("from", out var fromNode) ? fromNode.GetDouble() : 0;
                    var to = item.TryGetProperty("to", out var toNode) ? toNode.GetDouble() : 0;
                    var text = item.TryGetProperty("content", out var textNode) ? textNode.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    builder.AppendLine($"{from:F2} --> {to:F2}");
                    builder.AppendLine(text);
                }

                if (builder.Length > 0)
                {
                    return builder.ToString();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "下载字幕失败：{Url}", candidate.Url);
            }
        }

        return null;
    }

    private static IReadOnlyList<SponsorSegment> MergeSegments(IEnumerable<SponsorSegment> segments)
    {
        var ordered = segments
            .Where(p => p.End > p.Start)
            .OrderBy(p => p.Start)
            .ToList();

        var merged = new List<SponsorSegment>();
        foreach (var segment in ordered)
        {
            // 只合并「同一分类」的相邻段：赞助广告和三连提醒贴着的时候，
            // 合到一块会让结束时间顶到片尾，跳转目标超出时长就被拒掉了.
            if (merged.Count > 0 &&
                string.Equals(merged[^1].Category, segment.Category, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(merged[^1].Source, segment.Source, StringComparison.OrdinalIgnoreCase) &&
                segment.Start <= merged[^1].End + 0.5)
            {
                merged[^1].End = Math.Max(merged[^1].End, segment.End);
                continue;
            }

            merged.Add(new SponsorSegment
            {
                Start = segment.Start,
                End = segment.End,
                Category = segment.Category,
                Source = segment.Source,
                ActionType = segment.ActionType,
            });
        }

        return merged;
    }

    /// <summary>
    /// 自检开关：LocalState 下放一个 adskip-test.txt，就把整段视频当成广告片段，用来验证跳过链路.
    /// </summary>
    private static bool IsWholeVideoAdTestEnabled()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                "adskip-test.txt");
            return System.IO.File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 自检开关：LocalState 下放 adskip-manual.txt，就强制走手动模式（只弹提示不自动跳），
    /// 用来验证药丸提示与点击跳转.
    /// </summary>
    private static bool IsManualAdSkipTestEnabled()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path,
                "adskip-manual.txt");
            return System.IO.File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 自检：把 adskip-ai-test.txt 的内容当作字幕，验证 AI 识别链路.
    /// </summary>
    private string? ReadAiTestSubtitle()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path,
                "adskip-ai-test.txt");
            if (!System.IO.File.Exists(path))
            {
                return null;
            }

            var text = System.IO.File.ReadAllText(path);
            logger.LogInformation("AI 自检字幕已加载：{Length} 字", text.Length);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private void CheckSponsorBlock()
    {
        if (Player is null || Player.Duration <= 0)
        {
            HideSkipPrompt();
            return;
        }

        // 直播没有「广告片段」这回事，跳过提示只对点播生效.
        if (IsLive)
        {
            HideSkipPrompt();
            return;
        }

        // 整段当广告的自检模式：只看播放位置，不看片段表.
        if (IsWholeVideoAdTestEnabled())
        {
            if (Player.Position < Player.Duration - 3)
            {
                var testSegment = new SponsorSegment
                {
                    Start = 0,
                    End = Player.Duration - 1,
                    Category = "test",
                    Source = "test",
                };
                ShowSkipPrompt(testSegment);
                UpdateSkipPromptCountdown(testSegment, Player.Position);
            }
            else
            {
                HideSkipPrompt();
            }

            return;
        }

        if (_sponsorSegments.Count == 0)
        {
            HideSkipPrompt();
            return;
        }

        var manual = (SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockManualMode, false) || IsManualAdSkipTestEnabled()) && !IsForceAutoSkipTestEnabled();
        var position = Player.Position;
        var current = _sponsorSegments.FirstOrDefault(p => position >= p.Start && position < p.End - 0.3);
        if (current is null)
        {
            RestoreSponsoredMute();
            HideSkipPrompt();
            return;
        }

        if (!manual)
        {
            logger.LogInformation("进入待跳过区间：{Category} {Start:F1}-{End:F1}s，当前 {Position:F1}s，时长 {Duration:F1}s", current.CategoryName, current.Start, current.End, position, Player.Duration);
        }

        // mute 类片段：不跳，只在区间内把音量压掉（SponsorBlock 的 actionType=mute）.
        if (string.Equals(current.ActionType, "mute", StringComparison.OrdinalIgnoreCase))
        {
            MuteForSponsoredSegment();
            HideSkipPrompt();
            PushAdSkipLog($"静音段 {FormatSkipStamp(current.Start)}→{FormatSkipStamp(current.End)}，已压低音量");
            return;
        }

        RestoreSponsoredMute();

        if (manual)
        {
            // 手动模式：只弹提示，等用户点跳过.
            ShowSkipPrompt(current);
            UpdateSkipPromptCountdown(current, position);
            return;
        }

        // 目标夹到片尾前 0.3 秒：有些片段的结束时间正好是视频时长，直接拿去跳会被拒.
        var target = Math.Min(current.End, Math.Max(0, Player.Duration - 0.3));

        // 同一个目标 3 秒内只尝试一次：跳转没生效（例如缓冲中）时允许重试，
        // 否则一旦第一次 seek 被吞掉，这段就永远跳不过去.
        var isSameTarget = Math.Abs(_lastSponsorSkipPosition - target) <= 0.5;
        if (isSameTarget && DateTime.Now - _lastSponsorSkipTime < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastSponsorSkipPosition = target;
        _lastSponsorSkipTime = DateTime.Now;
        HideSkipPrompt();
        var skipMethod = current.Source switch
        {
            "ai" => "AI 识别",
            "cloud" => "云端广告库",
            "test" => "自检",
            _ => "空降助手",
        };
        PushAdSkipLog($"自动跳过（{skipMethod}）{FormatSkipStamp(current.Start)}→{FormatSkipStamp(current.End)}（{current.Duration:F0}s）");
        logger.LogInformation(
            "自动跳过{Category}：{Start:F1}s -> {End:F1}s（目标 {Target:F1}s，跳过 {Duration:F1}s）",
            current.CategoryName,
            current.Start,
            current.End,
            target,
            current.Duration);
        _ = ChangePositionAsync(target);
    }

    private void MuteForSponsoredSegment()
    {
        if (_sponsorMuteApplied)
        {
            return;
        }

        _sponsorMuteApplied = true;
        _sponsorMuteBackupVolume = Player?.Volume ?? 100;
        logger.LogInformation("片段要求静音：音量 {Volume} -> 0", _sponsorMuteBackupVolume);
        _ = ApplyVolumeAsync(0);
    }

    private void RestoreSponsoredMute()
    {
        if (!_sponsorMuteApplied)
        {
            return;
        }

        _sponsorMuteApplied = false;
        logger.LogInformation("静音片段结束，恢复音量 {Volume}", _sponsorMuteBackupVolume);
        _ = ApplyVolumeAsync(_sponsorMuteBackupVolume);
    }

    private void ShowSkipPrompt(SponsorSegment segment)
    {
        SkipPromptSegment = segment;
        IsSkipPromptVisible = true;
    }

    private void HideSkipPrompt()
    {
        if (IsSkipPromptVisible)
        {
            IsSkipPromptVisible = false;
        }

        SkipPromptSegment = default;
        SkipPromptText = "跳过广告片段";
    }

    /// <summary>
    /// 手动模式下跳过当前提示的片段.
    /// </summary>
    [RelayCommand]
    private async Task SkipPromptSegmentAsync()
    {
        if (SkipPromptSegment is null)
        {
            return;
        }

        var segment = SkipPromptSegment;
        var end = segment.End;
        _lastSponsorSkipPosition = end;
        HideSkipPrompt();
        PushAdSkipLog($"手动跳过 {FormatSkipStamp(segment.Start)}→{FormatSkipStamp(segment.End)}（{segment.Duration:F0}s）");
        logger.LogInformation(
            "手动跳过{Category}：{Start:F1}s -> {End:F1}s（跳过 {Duration:F1}s）",
            segment.CategoryName,
            segment.Start,
            segment.End,
            segment.Duration);
        await ChangePositionAsync(end);
    }
    /// <summary>
    /// 自检：LocalState/adskip-range.txt 每行写成 "开始,结束"（秒），直接当片段表用，
    /// 用来复现「片段结束时间正好等于视频时长」这类边界情况.
    /// </summary>
    private List<SponsorSegment> ReadSegmentRangeTest()
    {
        var result = new List<SponsorSegment>();
        try
        {
            var path = System.IO.Path.Combine(
                Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path,
                "adskip-range.txt");
            if (!System.IO.File.Exists(path))
            {
                return result;
            }

            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 ||
                    !double.TryParse(parts[0], out var start) ||
                    !double.TryParse(parts[1], out var end) ||
                    end <= start)
                {
                    continue;
                }

                result.Add(new SponsorSegment
                {
                    Start = start,
                    End = end,
                    Category = "sponsor",
                    Source = "test",
                });
            }

            logger.LogInformation("区间自检：载入 {Count} 段（{Ranges}）", result.Count, string.Join(" / ", result.Select(p => $"{p.Start:F0}-{p.End:F0}")));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取区间自检文件失败");
        }

        return result;
    }
    /// <summary>
    /// 自检：LocalState/adskip-auto.txt 存在时强制走自动跳过（覆盖设置里的手动模式）.
    /// </summary>
    private static bool IsForceAutoSkipTestEnabled()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path,
                "adskip-auto.txt");
            return System.IO.File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}












