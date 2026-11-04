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
    internal async Task InitializeAsync(MediaSnapshot snapshot)
    {
        _snapshot = snapshot;
        IsLive = snapshot.Type == BiliMediaType.Live;
        HookCdnStatus();
        _sponsorSegments = Array.Empty<SponsorSegment>();
        _lastSponsorSkipPosition = -1;
        _ = LoadSponsorBlockAsync(snapshot);
        _watchTracker = new PlaybackWatchTracker();
        _watchRecorded = false;
        ErrorMessage = default;
        CacheSpeedText = default;
        ShowPlayerWindow();
        IsConnecting = true;
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

        if (_historyResolver != null && Player != null)
        {
            await _historyResolver.SaveHistoryAsync(Player.Position, Player.Duration, Richasy.MpvKernel.Core.Enums.MpvPlayerState.End, true);
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
        else
        {
            Player = new PlayerSurface(new Richasy.MpvKernel.Player.MpvPlayer(Client!, _sourceResolver, _historyResolver, logger: logger));
            Player.PropertyChanged += OnPlayerPropertyChanged;
            Player.Mpv!.UpdateResolvers(_sourceResolver, _historyResolver);

            try
            {
                await Player.InitializeAsync();
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

        await ResetPlayerPresenterModeAsync();
        if (Window.IsClosed)
        {
            return;
        }

        CheckFullScreen();
        CheckCompactOverlay();
        ProgressChanged?.Invoke(this, 0);
        await LoadSourcesAsync();
        await LoadSubtitlesAsync();
        if (Window.IsClosed)
        {
            return;
        }

        if (UseWebEngine)
        {
            await LoadWebMediaAsync();
        }

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

        foreach (var item in Sources)
        {
            item.IsSelected = item.Equals(source);
        }

        var selected = Sources.FirstOrDefault(s => s.IsSelected);
        SelectedSource = selected;
        _continuePosition = Player?.Position;
        if (_sourceResolver is VideoMediaSourceResolver videoResolver)
        {
            _snapshot.PreferQuality = selected.Data.Quality;
            videoResolver.ResetSnapshot(_snapshot);
            await Player.ReplayAsync(Player.Position);
        }

        await Task.CompletedTask;
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

        var videoId = snapshot.Type == BiliMediaType.Video ? snapshot.Video?.Identifier.Id : null;
        if (string.IsNullOrEmpty(videoId) || _isSponsorLoading)
        {
            return;
        }

        _isSponsorLoading = true;
        try
        {
            // 三路来源合并：SponsorBlock（可换镜像）、云端广告库、AI 识别.
            var segments = new List<SponsorSegment>();
            var sponsorSegments = await SponsorBlockService.GetSegmentsAsync(videoId);
            segments.AddRange(sponsorSegments);
            logger.LogInformation("片段来源 sponsorblock：{Count} 段（服务器 {Server}）", sponsorSegments.Count, SponsorBlockService.CurrentServer);

            if (AdCloudService.IsEnabled)
            {
                var cloudSegments = await AdCloudService.GetSegmentsAsync(videoId, logger);
                segments.AddRange(cloudSegments);
                logger.LogInformation("片段来源 云端广告库：{Count} 段", cloudSegments.Count);
            }
            else
            {
                logger.LogInformation("云端广告库未启用，跳过该来源");
            }

            if (AiAdAnalyzer.IsEnabled)
            {
                var aiSegments = await AnalyzeAdsWithAiAsync(videoId, snapshot);
                segments.AddRange(aiSegments);
                logger.LogInformation("片段来源 AI 识别：{Count} 段（音频分析 {Audio}）", aiSegments.Count, AiAdAnalyzer.IsAudioEnabled ? "开" : "关");
            }
            else
            {
                logger.LogInformation("AI 识别未启用，跳过该来源");
            }

            _sponsorSegments = MergeSegments(segments);
            logger.LogInformation("跳过片段检查完成：{Count} 段", _sponsorSegments.Count);
            if (_sponsorSegments.Count > 0)
            {
                logger.LogInformation("共找到 {Count} 段可跳过片段（来源：{Sources}）", _sponsorSegments.Count,
                    string.Join("/", _sponsorSegments.Select(p => p.Source).Distinct()));
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

    private async Task<IReadOnlyList<SponsorSegment>> AnalyzeAdsWithAiAsync(string videoId, MediaSnapshot snapshot)
    {
        var subtitleText = await GetSubtitleTextAsync(snapshot);
        if (!string.IsNullOrWhiteSpace(subtitleText))
        {
            var result = await AiAdAnalyzer.AnalyzeSubtitleAsync(subtitleText, logger);
            if (result.Count > 0)
            {
                return result;
            }
        }

        if (!AiAdAnalyzer.IsAudioEnabled || _sourceResolver is null)
        {
            return Array.Empty<SponsorSegment>();
        }

        try
        {
            var source = await _sourceResolver.GetSourceAsync();
            var audioUrl = source?.Options?.AudioTracks?.FirstOrDefault();
            if (string.IsNullOrEmpty(audioUrl))
            {
                return Array.Empty<SponsorSegment>();
            }

            var cookie = this.Get<Richasy.BiliKernel.Bili.Authorization.IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;
            return await AiAdAnalyzer.AnalyzeAudioAsync(audioUrl, cookie, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "音频广告识别失败");
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
                return null;
            }

            var meta = metas.FirstOrDefault(p => !p.IsAI) ?? metas[0];
            var url = meta.Url?.ToString();
            if (string.IsNullOrEmpty(url))
            {
                return null;
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

    private static IReadOnlyList<SponsorSegment> MergeSegments(IEnumerable<SponsorSegment> segments)
    {
        var ordered = segments
            .Where(p => p.End > p.Start)
            .OrderBy(p => p.Start)
            .ToList();

        var merged = new List<SponsorSegment>();
        foreach (var segment in ordered)
        {
            // 与上一段重叠或几乎相接时合并，避免重复跳过.
            if (merged.Count > 0 && segment.Start <= merged[^1].End + 0.5)
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
            });
        }

        return merged;
    }

    private void CheckSponsorBlock()
    {
        if (_sponsorSegments.Count == 0 || Player is null || Player.Duration <= 0)
        {
            HideSkipPrompt();
            return;
        }

        var manual = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockManualMode, false);
        var position = Player.Position;
        var current = _sponsorSegments.FirstOrDefault(p => position >= p.Start && position < p.End - 0.3);
        if (current is null)
        {
            HideSkipPrompt();
            return;
        }

        if (manual)
        {
            // 手动模式：只弹提示，等用户点跳过.
            ShowSkipPrompt(current);
            return;
        }

        if (Math.Abs(_lastSponsorSkipPosition - current.End) > 0.5)
        {
            _lastSponsorSkipPosition = current.End;
            HideSkipPrompt();
            _ = ChangePositionAsync(current.End);
        }
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

        var end = SkipPromptSegment.End;
        _lastSponsorSkipPosition = end;
        HideSkipPrompt();
        await ChangePositionAsync(end);
    }
}
