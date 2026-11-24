// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using Humanizer;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Core.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Media;
using Windows.Storage.Streams;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型.
/// </summary>
public sealed partial class PlayerViewModel
{
    /// <summary>
    /// 初始化播放器.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    private async Task InitializeInternalAsync()
    {
        // 每个视频的开始都会走到这里（InitializeCoreAsync 里调用）：把位置回调链要用的设置与
        // 缓存刷一遍。位置回调每秒跑几十次，那里只允许读字段，不能再碰 WinRT.
        RefreshTickPathCache();

        if (Connector is null)
        {
            Connector = _snapshot.Type switch
            {
                BiliMediaType.Video => new VideoConnectorViewModel(this),
                BiliMediaType.Pgc => new PgcConnectorViewModel(this),
                _ => default,
            };

            if (Connector != null)
            {
                Connector.PlaylistInitialized += OnConnectorPlaylistInitialized;
                Connector.NewMediaRequest += OnConnectorNewMediaRequest;
                Connector.RequestOpenExtraPanel += OnRequestOpenExtraPanel;
                Connector.PropertiesUpdated += OnConnectorPropertiesUpdated;
            }
        }

        Subtitles.Clear();
        IsSubtitleEmpty = true;
        // 性能探针（三级）：检查点，进 PERFWM3:layout 的 detail 时间线（连接器已就绪）.
        Services.PerfWidgetMountTrace.Mark("connector-ready");

        if (Client?.IsDisposed != false)
        {
            // 性能探针（三级）：检查点，用来量 Windows.Media.Playback.MediaPlayer + SMTC 这段同步开销.
            Services.PerfWidgetMountTrace.Mark("native-mp-create");
            _nativeMp = new Windows.Media.Playback.MediaPlayer();
            _nativeMp.CommandManager.IsEnabled = false; // 禁用原生播放器的命令管理器.
            _smtc = _nativeMp.SystemMediaTransportControls;
            _smtc.IsEnabled = true; // 启用系统媒体控制.
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.ButtonPressed += OnSmtcButtonPressed;
            Services.PerfWidgetMountTrace.Mark("native-mp-ready");

            // 性能探针（三级）：检查点，用来量 DXGI 适配器枚举这段同步开销.
            Services.PerfWidgetMountTrace.Mark("gpu-names-start");
            var gpuNames = AppToolkit.GetGpuNames();
            Services.PerfWidgetMountTrace.Mark("gpu-names-ready");
            HasNvidiaGpu = gpuNames.Any(p => p.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            var mpvPath = SettingsToolkit.ReadLocalSetting(SettingNames.CustomLibmpvPath, string.Empty);
            if (string.IsNullOrEmpty(mpvPath) || !File.Exists(mpvPath))
            {
                var architecture = RuntimeInformation.ProcessArchitecture;
                var identifier = architecture == Architecture.Arm64 ? "arm64" : "x64";
                mpvPath = Path.Combine(Package.Current.InstalledPath, "Assets", "libmpv", identifier, "libmpv-2.dll");
            }

            var configPath = string.Empty;
            var decodeType = SettingsToolkit.ReadLocalSetting(SettingNames.PreferDecode, PreferDecodeType.Auto);
            if (decodeType == PreferDecodeType.Custom)
            {
                configPath = await AppToolkit.EnsureMpvConfigExistAsync();
            }

            var options = new MpvInitializeOptions
            {
                UseConfig = false,
                LoadScripts = true,
            };

            // 故障注入：用于验证「回落到网页播放器」链路（仅测试时设置环境变量）.
            var forceFail = Environment.GetEnvironmentVariable("BILICOPILOT_FORCE_MPV_FAIL") == "1"
                || File.Exists(Path.Combine(AppContext.BaseDirectory, "force_mpv_fail.flag"));
            if (forceFail)
            {
                throw new InvalidOperationException("已按测试开关模拟 mpv 初始化失败");
            }

            // mpv 的 "Device lost / DXGI_ERROR_DEVICE_RESET" 是 Client 这一层打出来的（不是 MpvPlayer），
            // 所以 client 的 logger 也得包上，否则恢复逻辑收不到信号.
            var clientWatch = Services.PlayerLifecycle.Begin($"PlayerVM#{LifecycleId}", "create-mpv-client");
            // 性能探针（三级）：检查点，进 PERFWM3:layout 的 detail 时间线（client 创建之前的同步活到此为止）.
            Services.PerfWidgetMountTrace.Mark("mpv-client-create-start");
            Client = await MpvClient.CreateAsync(mpvPath, options, logger: new MpvLogWatcher(logger, OnMpvSuspectLog));
            Services.PlayerLifecycle.End($"PlayerVM#{LifecycleId}", "create-mpv-client", clientWatch);
            // 性能探针（三级）：检查点，进 PERFWM3:layout 的 detail 时间线（区分 mpv client 创建与后面建实例）.
            Services.PerfWidgetMountTrace.Mark("mpv-client-ready");
            Client.ErrorOccurred += OnClientErrorOccurred;
            Client.CacheStateChanged += OnClientCacheStateChanged;
            SetSubtitleDelaySecondsCommand.Execute(SubtitleDelaySeconds);

            if (decodeType == PreferDecodeType.Auto)
            {
                await Client.SetVideoOutputAsync(VideoOutputType.GpuNext);
                await Client.SetGpuApiAsync(GpuApiType.D3D11);
                await Client.SetGpuContextAsync(GpuContextType.Auto);
                await Client.SetHardwareDecodeAsync(HardwareDecodeType.Auto);
            }
            else if (decodeType == PreferDecodeType.D3D11)
            {
                await Client.SetVideoOutputAsync(VideoOutputType.GpuNext);
                await Client.SetGpuApiAsync(GpuApiType.D3D11);
                await Client.SetGpuContextAsync(GpuContextType.D3D11);
                await Client.SetHardwareDecodeAsync(HardwareDecodeType.D3D11va);
            }
            else if (decodeType == PreferDecodeType.D3D12)
            {
                await Client.SetVideoOutputAsync(VideoOutputType.GpuNext);
                await Client.SetGpuApiAsync(GpuApiType.D3D11);
                await Client.SetGpuContextAsync(GpuContextType.D3D11);
                await Client.SetHardwareDecodeAsync(HardwareDecodeType.D3D12vaCopy);
            }
            else if (decodeType == PreferDecodeType.NVDEC)
            {
                await Client.SetVideoOutputAsync(VideoOutputType.GpuNext);
                await Client.SetGpuApiAsync(GpuApiType.D3D11);
                await Client.SetGpuContextAsync(GpuContextType.D3D11);
                await Client.SetHardwareDecodeAsync(HardwareDecodeType.Nvdec);
            }
            else if (decodeType == PreferDecodeType.Vulkan)
            {
                await Client.SetVideoOutputAsync(VideoOutputType.GpuNext);
                await Client.SetGpuApiAsync(GpuApiType.Vulkan);
                await Client.SetGpuContextAsync(GpuContextType.WindowsVulkan);
                await Client.SetHardwareDecodeAsync(HardwareDecodeType.Vulkan);
            }
            else if (decodeType == PreferDecodeType.Software)
            {
                await Client.SetHardwareDecodeAsync(HardwareDecodeType.None);
            }

            await Client.SetMuteAsync(IsMute);

            if (!string.IsNullOrEmpty(configPath))
            {
                var configLines = await File.ReadAllLinesAsync(configPath);
                MaxVolume = 130;

                // 查找以 volume-max 开头的行，获取等号后面的数字值.
                var volumeMaxLine = Array.Find(configLines, line => line.TrimStart().StartsWith("volume-max", StringComparison.OrdinalIgnoreCase));
                if (volumeMaxLine != null)
                {
                    // 提取等号后的数字值（等号后可能跟着空格）.
                    var match = Regex.Match(volumeMaxLine, @"=\s*(\d+(\.\d+)?)");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out var maxVolume))
                    {
                        MaxVolume = maxVolume;
                    }
                }

                await Client.SetConfigFileAsync(configPath);
            }
            else
            {
                // 未使用自定义配置文件时，使用附加设置.
                var preferAudioChannelLayout = SettingsToolkit.ReadLocalSetting(SettingNames.PreferAudioChannelLayout, Richasy.MpvKernel.Core.Enums.AudioChannelLayoutType.Auto);
                if (preferAudioChannelLayout != AudioChannelLayoutType.Auto)
                {
                    if (preferAudioChannelLayout == AudioChannelLayoutType.Custom)
                    {
                        await Client.SetAudioChannelLayoutAsync(AudioChannelLayoutType.Custom, ["7.1", "5.1", "stereo"]);
                    }
                    else
                    {
                        await Client.SetAudioChannelLayoutAsync(preferAudioChannelLayout);
                    }
                }

                var cacheSize = SettingsToolkit.ReadLocalSetting(SettingNames.MaxCacheSize, 300d);
                var cacheSeconds = SettingsToolkit.ReadLocalSetting(SettingNames.MaxCacheSeconds, 360d);
                var backCacheSize = SettingsToolkit.ReadLocalSetting(SettingNames.MaxBackCacheSize, 0d);
                await Client.SetDemuxerMaxBytesAsync($"{Convert.ToInt32(cacheSize)}MiB");
                await Client.SetDemuxerReadheadSecondsAsync(Convert.ToInt32(cacheSeconds));
                if (Convert.ToInt32(backCacheSize) > 0)
                {
                    await Client.SetDemuxerMaxBackBytesAsync($"{Convert.ToInt32(backCacheSize)}MiB");
                }

                var useCacheOnDisk = SettingsToolkit.ReadLocalSetting(SettingNames.CacheOnDisk, false);

                // 直播不写磁盘缓存：mpv 对直播流建文件缓存会失败（日志里 Failed to create file cache），
                // 失败后偶尔连视频轨都起不来.
                if (IsLive && useCacheOnDisk)
                {
                    useCacheOnDisk = false;
                    logger.LogInformation("直播已禁用磁盘缓存");
                }

                await Client.SetCacheOnDiskAsync(useCacheOnDisk);
                if (useCacheOnDisk)
                {
                    var cacheDir = SettingsToolkit.ReadLocalSetting(SettingNames.CacheDir, string.Empty);
                    if (!string.IsNullOrEmpty(cacheDir) && Directory.Exists(cacheDir))
                    {
                        try
                        {
                            await Client.SetCacheDirAsync(cacheDir);
                        }
                        catch
                        {
                        }
                    }
                }

                var profile = SettingsToolkit.ReadLocalSetting(SettingNames.MpvBuiltInProfile, MpvBuiltInProfile.HighQuality);
                if (profile != MpvBuiltInProfile.HighQuality)
                {
                    await Client.SetLoadAutoProfilesAsync(true);
                    await Client.SetBuiltInProfileAsync(profile.ToString().ToLowerInvariant());
                }

                var hrSeek = SettingsToolkit.ReadLocalSetting(SettingNames.HrSeek, HrSeekType.Default);
                await Client.SetHrSeekAsync(hrSeek);

                MaxVolume = SettingsToolkit.ReadLocalSetting(SettingNames.MaxVolume, 100d);
                await Client.SetMaxVolumeAsync(Convert.ToInt32(MaxVolume));

                var audioExclusive = SettingsToolkit.ReadLocalSetting(SettingNames.AudioExclusiveEnabled, false);
                await Client.SetAudioExclusiveAsync(audioExclusive);
            }

            // 直播临时开 Info，方便看视频轨为什么起不来.
            var logLevel = IsLive ? MpvLogLevel.Info : SettingsToolkit.ReadLocalSetting(SettingNames.MpvLogLevel, MpvLogLevel.Warn);
            await Client.SetLogLevelAsync(logLevel);
            await Client.UseIdleAsync(true);
            await Client.UseKeepOpenAsync(true);
            await Client.SetTlsVerifyAsync(false);
            await Client.SetAutoCreatePlaylistAsync(AutoCreatePlaylistKind.No);

            if (IsLive)
            {
                // 直播流用着色器/超分容易出现设备丢失，这里跳过。
                logger.LogDebug("直播跳过播放增强（着色器/超分）");
            }
            else
            {
                await RestorePlaybackEnhancementsAsync();
            }
        }

        if (_rightKeyLongPressTimer == null)
        {
            _rightKeyLongPressTimer = queue.CreateTimer();
            _rightKeyLongPressTimer.Interval = TimeSpan.FromMilliseconds(RightKeyLongPressDelay);
            _rightKeyLongPressTimer.Tick += OnRightKeyLongPressTimerTick;
        }

        if (_tripleTimer == null)
        {
            _tripleTimer = queue.CreateTimer();
            _tripleTimer.Interval = TimeSpan.FromSeconds(0.2);
            _tripleTimer.Tick += OnTripleTimerTick;
        }
    }

    private void OnRightKeyLongPressTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isRightKeyDown && !_isRightKeyTripleSpeed)
        {
            _isRightKeyTripleSpeed = true;
            TripleSpeedCommand.Execute(default);
        }

        _rightKeyLongPressTimer?.Stop();
    }

    private void OnTripleTimerTick(DispatcherQueueTimer sender, object args)
    {
        // 检查鼠标左键是否在按，同时检查键盘右方向键是否在按.
        var isLeftMouseClick = (PInvoke.GetAsyncKeyState(0x01) & 0x8000) != 0;
        var isRightArrowKeyPressed = (PInvoke.GetAsyncKeyState(0x27) & 0x8000) != 0;
        if (IsHoldingSpeedChanging && !isLeftMouseClick && !isRightArrowKeyPressed && !IsTouching)
        {
            RestoreSpeedCommand.Execute(default);
        }
    }

    private void OnConnectorPropertiesUpdated(object? sender, PlayerInformationUpdatedEventArgs e)
    {
        var updater = _smtc!.DisplayUpdater;
        //updater.AppMediaId = GetSnapshotItemId();
        updater.Type = MediaPlaybackType.Video;
        updater.VideoProperties.Title = e.Title;
        updater.VideoProperties.Subtitle = e.Subtitle;
        if (e.Cover is RandomAccessStreamReference streamRef)
        {
            updater.Thumbnail = streamRef;
        }
        else if (e.Cover is Uri uri)
        {
            updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
        }
        else
        {
            updater.Thumbnail = null;
        }

        updater.Update();

        // 关注/点赞/投币/收藏状态变了，结束页上的按钮要跟着刷新.
        RaiseEndScreenProperties();
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        queue.TryEnqueue(() =>
        {
            if (args.Button is SystemMediaTransportControlsButton.Play or SystemMediaTransportControlsButton.Pause)
            {
                PlayPauseCommand.Execute(default);
            }
            else if (args.Button == SystemMediaTransportControlsButton.Next && IsNextButtonEnabled)
            {
                PlayNextCommand.Execute(default);
            }
            else if (args.Button == SystemMediaTransportControlsButton.Previous && IsPrevButtonEnabled)
            {
                PlayPreviousCommand.Execute(default);
            }
        });
    }

    private async Task RestorePlaybackEnhancementsAsync()
    {
        if (Client is null)
        {
            return;
        }

        // 直播不恢复着色器/超分：Anime4K、NNEDI3、VSR 压在直播流上会让视频轨起不来（只剩声音、画面一片白）.
        if (IsLive)
        {
            logger.LogInformation("直播不恢复播放增强（着色器/超分只对点播生效）");
            return;
        }

        var remember = SettingsToolkit.ReadLocalSetting(SettingNames.RememberPlaybackEnhancements, true);
        if (!remember)
        {
            // 关闭「记住」后一律从零开始，并清掉旧值，避免下次打开又冒出来.
            Anime4KMode = Anime4KMode.None;
            ArtCNNMode = ArtCNNMode.None;
            Nnedi3Mode = Nnedi3Mode.None;
            RavuMode = RavuMode.None;
            VsrScale = VsrScale.None;
            SettingsToolkit.WriteLocalSetting(SettingNames.Anime4KMode, Anime4KMode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.ArtCNNMode, ArtCNNMode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.Nnedi3Mode, Nnedi3Mode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.RavuMode, RavuMode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.NvidiaVsrScale, VsrScale.None);
            logger.LogInformation("已关闭播放增强记忆，本次不恢复着色器与超分");
            return;
        }

        Anime4KMode = ReadEnhancement(SettingNames.Anime4KMode, Anime4KMode.None);
        ArtCNNMode = ReadEnhancement(SettingNames.ArtCNNMode, ArtCNNMode.None);
        Nnedi3Mode = ReadEnhancement(SettingNames.Nnedi3Mode, Nnedi3Mode.None);
        RavuMode = ReadEnhancement(SettingNames.RavuMode, RavuMode.None);
        VsrScale = ReadEnhancement(SettingNames.NvidiaVsrScale, VsrScale.None);

        logger.LogInformation(
            "恢复播放增强：Anime4K={Anime4K} ArtCNN={ArtCNN} NNEDI3={Nnedi3} RAVU={Ravu} VSR={Vsr}",
            Anime4KMode,
            ArtCNNMode,
            Nnedi3Mode,
            RavuMode,
            VsrScale);

        try
        {
            var shaders = GetCombinedShaders();
            if (shaders.Length > 0)
            {
                await Client.SetShadersAsync(shaders);
            }

            CheckClearShaderEnabled();

            if (VsrScale != VsrScale.None)
            {
                await ApplyVsrAsync(VsrScale);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore playback enhancements failed");
        }
    }
    /// <summary>
    /// 载入完成后强制再应用一次记住的着色器与超分.
    /// 「记住着色器与超分设置」原来只在 mpv 客户端初始化时恢复一次，
    /// 换视频（复用播放器或重建实例）走的是另一条路径，等于没恢复。
    /// 这里在每次媒体加载完之后补一遍；开关关掉时什么都不做.
    /// </summary>
    /// <returns>任务.</returns>
    internal async Task ReapplyRememberedEnhancementsAsync()
    {
        if (Client is null || IsLive || Player is null || Player.IsWebEngine)
        {
            return;
        }

        if (!SettingsToolkit.ReadLocalSetting(SettingNames.RememberPlaybackEnhancements, true))
        {
            return;
        }

        Anime4KMode = ReadEnhancement(SettingNames.Anime4KMode, Anime4KMode.None);
        ArtCNNMode = ReadEnhancement(SettingNames.ArtCNNMode, ArtCNNMode.None);
        Nnedi3Mode = ReadEnhancement(SettingNames.Nnedi3Mode, Nnedi3Mode.None);
        RavuMode = ReadEnhancement(SettingNames.RavuMode, RavuMode.None);
        VsrScale = ReadEnhancement(SettingNames.NvidiaVsrScale, VsrScale.None);

        try
        {
            var shaders = GetCombinedShaders();
            if (shaders.Length > 0)
            {
                await Client.SetShadersAsync(shaders);
            }

            CheckClearShaderEnabled();

            if (VsrScale != VsrScale.None)
            {
                await ApplyVsrAsync(VsrScale);
            }

            logger.LogInformation(
                "载入后重放着色器/超分：Anime4K={Anime4K} ArtCNN={ArtCNN} NNEDI3={Nnedi3} RAVU={Ravu} VSR={Vsr}",
                Anime4KMode,
                ArtCNNMode,
                Nnedi3Mode,
                RavuMode,
                VsrScale);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "载入后重放着色器/超分失败");
        }
    }

    private void OnClientCacheStateChanged(object? sender, MpvCacheStateEventArgs e)
    {
#if DEBUG
        // 缓冲欠载 / 恢复也算稳定期：这段时间的「停滞」不是要抓的那一种.
        NotifyPlaybackStallSettling();
#endif
        CacheStateChanged?.Invoke(this, e);
    }

    private async void OnClientErrorOccurred(object? sender, MpvError e)
    {
        // mpv 的错误回调可能比 Player 实例活得久：重建路径里中间有 Player = null 的窗口期，
        // 这期间抛上来的错误会让下面的 Player.Duration 直接空引用（表现为 c0000005 闪退）.
        var player = Player;
        if (player is null || _disposed)
        {
            Services.PlayerLifecycle.Log("error", $"PlayerVM#{LifecycleId}", $"mpv error {e} ignored (no player)");
            return;
        }

        if (e == MpvError.TlsError)
        {
            if (!_isTlsFailed)
            {
                _isTlsFailed = true;
                return;
            }

            return;
        }
        else if (e == MpvError.VoInitFailed)
        {
            // 视频输出初始化失败时，先回退着色器/超分并重建输出，避免一直白屏.
            await RecoverPlaybackEnhancementsAsync();
        }
        else if (player.Duration < 1)
        {
            if (!_isBroken)
            {
                _isBroken = true;

                // 卡顿时按候选池轮换 CDN 节点，再重试一次.
                if (CdnAccelerator.StallRecovery && CdnAccelerator.IsEnabled && !_isCdnRotated)
                {
                    _isCdnRotated = true;
                    var nextHost = CdnAccelerator.RotateHost();
                    logger.LogInformation("播放失败，切换 CDN 节点到 {Host}", nextHost);
                }

                await player.ReplayAsync();
                return;
            }

            // 重试仍然失败，回落到网页播放器（Shaka）.
            if (await TryFallbackToWebEngineAsync(player.Position))
            {
                return;
            }
        }

        queue.TryEnqueue(() =>
        {
            var errorContent = e switch
            {
                MpvError.Nomem => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorNoMemory),
                MpvError.Uninitialized => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorUninitialized),
                MpvError.LoadingFailed => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorLoadingFailed),
                MpvError.AoInitFailed => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorAudioFailed),
                MpvError.VoInitFailed => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorVideoFailed),
                MpvError.NothingToPlay => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorNotingToPlay),
                MpvError.UnknownFormat => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorUnknownFormat),
                MpvError.Unsupported => ResourceToolkit.GetLocalizedString(StringNames.MpvErrorUnsupported),
                _ => default,
            };

            if (!string.IsNullOrEmpty(errorContent) && (player.Duration < 1 || e == MpvError.PassthroughFormatUnsupported))
            {
                ErrorMessage = errorContent;
            }
            else
            {
                WarningOccurred?.Invoke(this, ResourceToolkit.GetLocalizedString(StringNames.MpvErrorGeneric));
            }

            CheckBlackBackgroundVisible();
        });
    }

    private async Task ResetPlayerPresenterModeAsync()
    {
        if (Window is null || Window.GetWindow() == null || Window.IsClosed)
        {
            return;
        }

        var currentWindowPresenter = Window!.GetWindow().Presenter.Kind;
        if (currentWindowPresenter == AppWindowPresenterKind.FullScreen)
        {
            await ApplyFullScreenAsync(true);
        }
        else if (currentWindowPresenter == AppWindowPresenterKind.CompactOverlay)
        {
            await ApplyCompactOverlayAsync(true);
        }
    }

    private void CheckFullScreen()
    {
        if (Window is null || Client is null || Window.IsClosed)
        {
            return;
        }

        if (Player.IsFullScreen && Window.GetWindow().Presenter.Kind != AppWindowPresenterKind.FullScreen)
        {
            Window.EnterFullScreen();
        }
        else if (!Player.IsFullScreen && Window.GetWindow().Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            Window.ExitFullScreen();
        }
    }

    private void CheckCompactOverlay()
    {
        if (Window is null || Window.IsClosed)
        {
            return;
        }

        if (Player.IsCompactOverlay && Window.GetWindow().Presenter.Kind != AppWindowPresenterKind.CompactOverlay)
        {
            Window.EnterCompactOverlay();
        }
        else if (!Player.IsCompactOverlay && Window.GetWindow().Presenter.Kind == AppWindowPresenterKind.CompactOverlay)
        {
            Window.ExitCompactOverlay();
        }
    }

    private void ShowMainWindow()
    {
        var mainWnd = this.Get<Core.AppViewModel>().Windows.Find(p => p is MainWindow);
        if (!this.Get<Core.AppViewModel>().IsClosed && mainWnd is not null)
        {
            var isMax = PInvoke.IsZoomed(new(Window!.Handle));
            if (!isMax)
            {
                var currentPos = Window!.GetWindow().Position;
                var currentSize = Window.GetWindow().Size;
                if (Window.GetWindow().Presenter.Kind == AppWindowPresenterKind.Overlapped)
                {
                    mainWnd.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(currentPos.X, currentPos.Y, currentSize.Width, currentSize.Height));
                }
            }

            mainWnd.Activate();
        }
    }

    private void CheckBlackBackgroundVisible()
        => IsBlackBackgroundVisible = Player?.IsPlaybackInitialized != true || !string.IsNullOrEmpty(ErrorMessage);

    private void OnWindowSizeChanged(object? sender, Size e)
    {
        queue.TryEnqueue(() =>
        {
            var isVertical = e.Height > e.Width;
            if (isVertical != IsVerticalScreen)
            {
                IsVerticalScreen = isVertical;
                ResetSubtitlePositionCommand.Execute(default);
            }
        });
    }

    private async void OnWindowDestroying(AppWindow sender, object args)
    {
        try
        {
            await HandleWindowDestroyingAsync();
        }
        catch (Exception ex)
        {
            // async void 里抛出去就是未处理异常，直接结束进程：必须自己兜.
            Services.PlayerLifecycle.Log("dispose", $"PlayerVM#{LifecycleId}", $"window destroying failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 关闭播放窗口的收尾顺序（对照 BiliFluent 的 PlayerWindow / 播放会话定的）：
    /// 1. 先把主窗口还回来（用户看到的应该是主窗口，不是一块黑屏）；
    /// 2. 再取消在飞的初始化并拆播放器；
    /// 3. 集成模式下最后才关主窗口。
    /// 这里不复制它「每次关闭都销毁 mpv 实例」的策略：本项目切视频要靠复用实例才不崩，
    /// 只有窗口真的没了才拆.
    /// </summary>
    /// <returns>任务.</returns>
    private async Task HandleWindowDestroyingAsync()
    {
        var appVM = this.Get<AppViewModel>();
        var hideMainWindowOnPlay = SettingsToolkit.ReadLocalSetting(SettingNames.HideMainWindowOnPlay, true);
        if (hideMainWindowOnPlay && !UseIntegrationOperation)
        {
            ShowMainWindow();
        }
        else if (appVM.Players.Count <= 1 && !appVM.IsMainWindowVisible() && !appVM.IsClosed && !(hideMainWindowOnPlay && UseIntegrationOperation))
        {
            ShowMainWindow();
        }

        await DisposeAsync();
        if (UseIntegrationOperation)
        {
            var mainWnd = this.Get<Core.AppViewModel>().Windows.Find(p => p is MainWindow);
            mainWnd?.Close();
        }
    }

    private void TrackWatchedVideo()
    {
        // Player 可能是 null：初始化 20 秒超时那条路会把 Player 置空却**故意不 Dispose** 卡住的
        // surface（见 InitializeCoreAsync 的 TimeoutException 分支），而那个 surface 还连着 mpv，
        // 会继续以 ~60Hz 抛 Position/PlaybackState。2026-11-24 21:31:31 那次实测：超时后旧 surface
        // 继续推事件，116 秒里这里空引用了 6,438 次，把日志整个冲掉。
        var player = Player;
        if (player is null || _watchRecorded || IsConnecting || _snapshot is null || _snapshot.IsPrivate)
        {
            return;
        }

        if (!_watchTracker.Observe(player.Position, System.Diagnostics.Stopwatch.GetElapsedTime(0),
            player.PlaybackState == MpvPlayerState.Playing, player.PlaybackRate))
        {
            return;
        }

        var key = _snapshot.Type switch
        {
            BiliMediaType.Video => $"video_{_snapshot.Video!.Identifier.Id}",
            // _episode 是 EpisodeInformation?（PgcConnectorViewModel.Properties.cs:22），
            // 构造期虽然一定会赋值，但 FindInitialEpisode 可能返回 null，所以这里按可空处理.
            BiliMediaType.Pgc when Connector is PgcConnectorViewModel pgc && pgc._episode is not null
                => $"episode_{pgc._episode.Identifier.Id}",
            _ => null,
        };
        if (key is not null)
        {
            _watchRecorded = true;
            queue.TryEnqueue(() => MomentWatchHistoryToolkit.MarkWatched(key));
        }
    }

    private async void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // async void 里漏出去的异常没有任何人接，直接杀掉进程——而 mpv 是独立线程/HWND，
        // 它还会继续播，看起来就像"UI 崩了但视频正常". 这里兜一层，至少留下日志.
        try
        {
            await HandlePlayerPropertyChangedAsync(e);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理播放器属性变更时出错：{Property}", e.PropertyName);
        }
    }

    private async Task HandlePlayerPropertyChangedAsync(PropertyChangedEventArgs e)
    {
        // 这个方法里的每条分支都作用于 Player，而 Player 确实会是 null：
        //   ① 重建窗口期——InitializeCoreAsync 先 Dispose 旧 surface 再把 Player 置空，之后才建新的；
        //   ② 初始化 20 秒超时——catch 里 `Player = null` 但**故意不 Dispose** 那个卡住的 surface
        //      （同时 Dispose 会撞原生层），而它仍然连着 mpv，会继续以 ~60Hz 抛 Position/PlaybackState。
        // ② 的旧事件会一直发下去（surface 没人 Dispose），所以必须在入口丢掉：
        // 没有 Player 就没有任何可更新的状态，继续往下走只会是另一个空引用.
        if (Player is null)
        {
            return;
        }

        // 诊断（Debug）：只消费既有通知，不做任何新的 mpv 查询.
#if DEBUG
        ObservePlaybackForStall(e.PropertyName);
#endif

        if (e.PropertyName is nameof(Player.Position) or nameof(Player.PlaybackState))
        {
            TrackWatchedVideo();
        }

        // mpv 的属性变更不保证在 UI 线程上抛，而 IsConnecting 绑着 XAML 的 Visibility，
        // 跨线程直接改会碰 UI（实测闪退：事件日志 coreclr / Xaml.Controls + c0000005），
        // 所以统一回 UI 线程处理.
        if (e.PropertyName == nameof(Player.PlaybackState)
            && IsConnecting
            && Player is not null
            && Player.Duration > 1
            && Player.PlaybackState is MpvPlayerState.Playing or MpvPlayerState.Paused)
        {
            var dispatcher = this.Get<Microsoft.UI.Dispatching.DispatcherQueue>();
            if (dispatcher.HasThreadAccess)
            {
                StopConnectWatchdog();
                IsConnecting = false;
            }
            else
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (IsConnecting)
                    {
                        StopConnectWatchdog();
                        IsConnecting = false;
                    }
                });
            }
        }

        if (e.PropertyName == nameof(Player.Position))
        {
            // 这条分支每个 tick（约 20~60 次/秒）都跑：下面只做字段读取与内存比较，
            // 不再碰 WinRT（设置读的是 RefreshTickPathCache 缓存下来的字段），
            // 也不再每 tick 格式化一次时间字符串.
            var position = Player.Position;
            IsPreviewProgressChanging = false;
            var wholeSecond = (int)position;
            if (wholeSecond != _positionTextSecond)
            {
                // PositionText 只在整秒变化时更新：原来每 tick 一次 TimeSpan.FromSeconds +
                // ToString 会白扔一个字符串，而显示粒度本来就只有秒.
                _positionTextSecond = wholeSecond;
                PositionText = TimeSpan.FromSeconds(wholeSecond).ToString(@"hh\:mm\:ss");
            }

            ProgressChanged?.Invoke(this, position);
            CheckSponsorBlock();
            CheckDanmakuJump();
#if DEBUG
            // Debug：这一段顺便量「一次状态读取 + 消费」的耗时，供停滞监视器判定.
            UpdateDanmakuPositionForStall(position);
#else
            Danmaku.UpdatePosition(Convert.ToInt32(position));
#endif
            UpdateChapterPlaybackState(position);

            if (Player.Duration > 5)
            {
                IsEnd = false;
                var isShowTip = !_isPlayNextWithoutTip;
                var endSec = isShowTip ? 2 : 0.1;
                if (Math.Abs(Player.Position - Player.Duration) < endSec)
                {
                    // 到底了：先把速度、自动连播这些处理掉.
                    // 结束页不在这里出现——这里最多提前 2 秒就会进，要等播放器真的停下
                    // （Paused / Stopped）再切界面，见下面按 PlaybackState 的判断.
                    IsHoldingSpeedChanging = false;
                    if (Math.Abs(_lastSpeed - Player.PlaybackRate) > 0.01)
                    {
                        await ApplySpeedAsync(_lastSpeed);
                    }

                    if (_isTlsFailed)
                    {
                        // 如果 TLS 失败了，可能会导致播放器无法正确获取位置，所以这里不处理跳过逻辑.
                        ErrorMessage = ResourceToolkit.GetLocalizedString(StringNames.SeekFailed);
                        CheckBlackBackgroundVisible();
                        return;
                    }

                    // 因为使用了 keep-open，所以视作播放结束.
                    var autoPlayNext = _isAutoPlayNext;
                    if (autoPlayNext && IsNextButtonEnabled && !IsConnecting)
                    {
                        if (isShowTip)
                        {
                            if (!IsNextTipShown && !IsNextTipShownInThisMedia)
                            {
                                IsNextTipShownInThisMedia = true;
                                RequestShowNextTip?.Invoke(this, EventArgs.Empty);
                            }
                        }
                        else
                        {
                            PlayNextCommand.Execute(default);
                            _isTlsFailed = false;
                            return;
                        }
                    }
                    else
                    {
                        IsEnd = true;
                        IsControlsVisible = true;
                    }

                    // 切结束页的时机：位置真的到片尾（最后 0.3 秒），
                    // 或者位置已经不再推进（用防抖定时器判断，见 ScheduleEndSettleCheck）.
                    // 只靠 PlaybackState 不行——片尾那一刻 mpv 上报的仍是 Playing.
                    if (Player.Position >= Player.Duration - 0.3
                        || Player.PlaybackState is MpvPlayerState.Paused or MpvPlayerState.Idle)
                    {
                        if (!IsPlaybackEnded)
                        {
                            logger.LogInformation(
                                "播放到片尾，切结束页：position={Position:F2} duration={Duration:F2} state={State}",
                                Player.Position,
                                Player.Duration,
                                Player.PlaybackState);
                        }

                        MarkPlaybackEnded();
                    }
                    else
                    {
                        // 还在最后一两秒里、位置仍在推进：起个防抖，等它停下来再切.
                        ScheduleEndSettleCheck();
                    }
                }
                else if (Math.Abs(Player.Position - Player.Duration) > 2 && IsNextTipShown)
                {
                    RequestHideNextTip?.Invoke(this, EventArgs.Empty);
                }

                if (Player.PlaybackState == MpvPlayerState.Playing && _isTlsFailed)
                {
                    _isTlsFailed = false;
                }

                // 位置已经离开片尾（用户往回拖了），退出结束状态.
                if (Math.Abs(Player.Position - Player.Duration) > 2)
                {
                    ClearPlaybackEnded();
                }

                _prevPosition = Player.Position;            }
            else if (Player.Duration < 5)
            {
                return;
            }
        }
        else if (e.PropertyName == nameof(Player.PlaybackState))
        {
            // 状态变成暂停/空闲、并且就停在片尾，这时切结束页.
            // 注意：不要在这里用"状态回到 Playing 就退出结束状态"——实测片尾时 mpv 仍会上报
            // Playing，那条规则会把刚出现的结束页立刻清掉（上一版结束页不出现的原因）。
            // 退出结束状态只走位置判断（往回拖超过 2 秒）或重新加载.
            if (!IsLive
                && Player.Duration > 5
                && Math.Abs(Player.Position - Player.Duration) < 1
                && Player.PlaybackState is MpvPlayerState.Paused or MpvPlayerState.Idle)
            {
                MarkPlaybackEnded();
            }
        }
        else if (e.PropertyName == nameof(Player.Duration))
        {
            DurationText = TimeSpan.FromSeconds(Player.Duration).ToString(@"hh\:mm\:ss");
            ChapterInitialized?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(Player.IsFullScreen))
        {
            CheckFullScreen();
        }
        else if (e.PropertyName == nameof(Player.IsCompactOverlay))
        {
            CheckCompactOverlay();
        }
        else if (e.PropertyName == nameof(Player.Title))
        {
            Window!.GetWindow().Title = Player.Title;
        }
        else if (e.PropertyName == nameof(Player.Volume) && Player.IsPlaybackInitialized)
        {
            if (IsVolumeChanging)
            {
                if (DateTimeOffset.Now - LastVolumeChangingTime < TimeSpan.FromMilliseconds(500))
                {
                    return;
                } 

                IsVolumeChanging = false;
            }

            CurrentVolume = Player.Volume;
            SettingsToolkit.WriteLocalSetting(SettingNames.PlayerVolume, Player.Volume);
        }
        else if (e.PropertyName == nameof(Player.PlaybackRate) && Player.IsPlaybackInitialized)
        {
            Danmaku.ExtraSpeed = Player.PlaybackRate;
            if (_isPlayerSpeedShare)
            {
                SettingsToolkit.WriteLocalSetting(SettingNames.PlayerSpeed, Player.PlaybackRate);
            }
        }
        else if (e.PropertyName == nameof(Player.IsPlaybackInitialized) && Player.IsPlaybackInitialized && !(Window?.IsClosed ?? true))
        {
            ErrorMessage = default;
            _prevPosition = 0;
            CheckBlackBackgroundVisible();
            CheckBottomProgressBarVisible();
            ResetSubtitlePositionCommand.Execute(default);
            ReloadFontsCommand.Execute(default);
            ChangeSubtitleFontSizeCommand.Execute(Math.Max(1, _subtitleFontSize));
            if (_continuePosition > 0)
            {
                await ApplySeekAsync(_continuePosition!.Value);
                _continuePosition = null;
            }

            // 强制关闭可能开启的统计信息覆盖层.
            await Client!.SendKeyPressAsync("ESC");
            IsStatsOverlayShown = false;

            // Make sure playing.
            if (Player.PlaybackState != MpvPlayerState.Playing)
            {
                await ApplyResumeAsync();
            }

            await VerifyChaptersAsync();

            if (!_isDanmakuInitialized)
            {
                InitializeDanmakuCommand.Execute(default);
                _isDanmakuInitialized = true;
            }
        }
        else if (e.PropertyName == nameof(Player.CacheSpeed))
        {
            CacheSpeedText = Player.CacheSpeed < 1 ? default : Player.CacheSpeed.Bytes().Humanize() + "/s";
        }
        else if (e.PropertyName == nameof(Player.PlaybackState))
        {
            if (IsDanmakuControlVisible && Danmaku.IsShowDanmaku)
            {
                if (Player.PlaybackState == MpvPlayerState.Playing)
                {
                    Danmaku.Resume();
                }
                else
                {
                    Danmaku.Pause();
                }
            }

            if (Player.PlaybackState is MpvPlayerState.Buffering or MpvPlayerState.Seeking && !string.IsNullOrEmpty(ErrorMessage))
            {
                ErrorMessage = default;
                CheckBlackBackgroundVisible();
            }

            _smtc?.PlaybackStatus = Player.PlaybackState switch
            {
                MpvPlayerState.Playing => MediaPlaybackStatus.Playing,
                MpvPlayerState.Paused => MediaPlaybackStatus.Paused,
                MpvPlayerState.Buffering or MpvPlayerState.Buffering => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Stopped,
            };
        }
    }

    // ---- 播放位置回调链的缓存 ----
    // 下面这些全是纯内存字段：位置回调（每秒 20~60 次）只读它们，不再做 WinRT 读取、
    // 也不再做周期性字符串格式化。设置在 RefreshTickPathCache 里每个视频刷新一次.

    /// <summary>
    /// 上一次写进 <see cref="PositionText"/> 的整秒值；<see cref="int.MinValue"/> 表示还没写过.
    /// </summary>
    private int _positionTextSecond = int.MinValue;

    /// <summary>
    /// 当前章节索引（最后一个 Position ≤ 播放位置的章节），-1 表示第一个章节还没开始.
    /// </summary>
    private int _chapterPlayedIndex = -1;

    /// <summary>
    /// 上一次被点亮 IsPlaying 的章节索引，-1 表示没有.
    /// </summary>
    private int _chapterPlayingIndex = -1;

    private bool _isPlayNextWithoutTip;
    private bool _isAutoPlayNext;
    private bool _isPlayerSpeedShare;
    private double _subtitleFontSize = 32d;

    /// <summary>
    /// 每个视频开始时刷一次位置回调链要用的设置与缓存.
    /// </summary>
    /// <remarks>
    /// <see cref="SettingsToolkit.ReadLocalSetting{T}"/> 是「服务定位器 + WinRT 本地设置容器」，
    /// 中间没有缓存层（Toolkits/SettingsToolkit.cs:22 → GlobalDependencies.Kernel.GetRequiredService），
    /// 放在播放位置回调里就是每个 tick 都跨一次容器边界。这四项只在「接近片尾 / 换倍速 /
    /// 开始播放」时才需要，所以每个视频读一次就够；改了设置后从下一部视频生效。
    /// （倍速共享不用等下一部：它本来就写回同一个设置键，下次读到的就是新值.）
    /// </remarks>
    private void RefreshTickPathCache()
    {
        _positionTextSecond = int.MinValue;
        _chapterPlayedIndex = -1;
        _chapterPlayingIndex = -1;
        _isPlayNextWithoutTip = SettingsToolkit.ReadLocalSetting(SettingNames.PlayNextWithoutTip, false);
        _isAutoPlayNext = SettingsToolkit.ReadLocalSetting(SettingNames.AutoPlayNext, true);
        _isPlayerSpeedShare = SettingsToolkit.ReadLocalSetting(SettingNames.IsPlayerSpeedShare, true);
        _subtitleFontSize = SettingsToolkit.ReadLocalSetting(SettingNames.SubtitleFontSize, 32d);
    }

    /// <summary>
    /// 按当前位置更新章节的「已播放 / 正在播放」标记.
    /// </summary>
    /// <param name="position">当前播放位置（秒）.</param>
    /// <remarks>
    /// 原来的实现每个 tick 都全量遍历 <see cref="Chapters"/>、并给每个章节写两个属性
    /// （每次写都会发一次属性通知），章节多的视频里这是位置回调链上最贵的一段。
    /// 这里改成：从上一次命中的章节出发挪到当前章节（正常播放只走 0~1 步，跳转时才走一段），
    /// 并且只写真正需要变的属性。判定条件与原实现完全一致：
    /// <c>IsPlayed = Position ≤ 播放位置</c>、<c>IsPlaying = |Position - 播放位置| &lt; 2</c>。
    /// </remarks>
    private void UpdateChapterPlaybackState(double position)
    {
        var chapters = Chapters;
        var count = chapters.Count;
        if (count == 0)
        {
            return;
        }

        // 当前章节 = 最后一个 Position ≤ position 的章节；-1 表示第一个章节还没开始.
        var played = Math.Clamp(_chapterPlayedIndex, -1, count - 1);
        while (played + 1 < count && chapters[played + 1].Position <= position)
        {
            played++;
        }

        while (played >= 0 && chapters[played].Position > position)
        {
            played--;
        }

        if (played != _chapterPlayedIndex)
        {
            if (played > _chapterPlayedIndex)
            {
                for (var i = Math.Max(0, _chapterPlayedIndex); i <= played; i++)
                {
                    if (!chapters[i].IsPlayed)
                    {
                        chapters[i].IsPlayed = true;
                    }
                }
            }
            else
            {
                // 往回拖：跳过的章节要褪回未播放.
                for (var i = Math.Max(0, played + 1); i <= _chapterPlayedIndex && i < count; i++)
                {
                    if (chapters[i].IsPlayed)
                    {
                        chapters[i].IsPlayed = false;
                    }
                }

                if (played >= 0 && !chapters[played].IsPlayed)
                {
                    chapters[played].IsPlayed = true;
                }
            }

            _chapterPlayedIndex = played;
        }

        // 高亮窗口与原逻辑一致（±2 秒），命中的可能是当前章节，也可能是下一个.
        var nextIndex = played + 1;
        var isPlayingCurrent = played >= 0 && Math.Abs(chapters[played].Position - position) < 2;
        var isPlayingNext = nextIndex < count && Math.Abs(chapters[nextIndex].Position - position) < 2;

        if (_chapterPlayingIndex >= 0
            && _chapterPlayingIndex < count
            && _chapterPlayingIndex != played
            && _chapterPlayingIndex != nextIndex)
        {
            chapters[_chapterPlayingIndex].IsPlaying = false;
        }

        if (played >= 0 && chapters[played].IsPlaying != isPlayingCurrent)
        {
            chapters[played].IsPlaying = isPlayingCurrent;
        }

        if (nextIndex < count && chapters[nextIndex].IsPlaying != isPlayingNext)
        {
            chapters[nextIndex].IsPlaying = isPlayingNext;
        }

        _chapterPlayingIndex = isPlayingCurrent ? played : (isPlayingNext ? nextIndex : -1);
    }

    private async void OnConnectorNewMediaRequest(object? sender, MediaSnapshot e)
    {
        if (_snapshot == e)
        {
            return;
        }

        await ApplyPauseAsync();
        await Client!.StopAsync();
        await InitializeAsync(e);
    }

    private void OnConnectorPlaylistInitialized(object? sender, PlaylistInitializedEventArgs e)
    {
        IsPrevButtonEnabled = e.CanPrev;
        IsNextButtonEnabled = e.CanNext;
        PrevButtonToolTip = string.Format(ResourceToolkit.GetLocalizedString(StringNames.PrevVideoTemplate), e.PrevTitle);
        NextButtonToolTip = string.Format(ResourceToolkit.GetLocalizedString(StringNames.NextVideoTemplate), e.NextTitle);
        IsVideoNavigationAvailable = e.CanPrev || e.CanNext;
        if (_smtc != null)
        {
            _smtc.IsNextEnabled = IsNextButtonEnabled;
            _smtc.IsPreviousEnabled = IsPrevButtonEnabled;
        }
    }

    private void OnRequestOpenExtraPanel(object? sender, EventArgs e)
    {
        if (IsControlsVisible)
        {
            IsControlsVisible = false;
        }

        IsExtraPanelVisible = true;
    }

    private void OnWindowPointerReleased(InputPointerSource sender, PointerEventArgs args)
    {
        if ((args.CurrentPoint.Properties.PointerUpdateKind is PointerUpdateKind.XButton1Released or PointerUpdateKind.XButton2Released) && UseIntegrationOperation)
        {
            BackCommand.Execute(default);
            args.Handled = true;
        }
    }

    private void OnWindowKeyDown(InputKeyboardSource sender, KeyEventArgs args)
    {
        if (IsControlsVisible || IsPopupVisible || IsExtraPanelVisible)
        {
            return;
        }

        if (Player is null || Player.PlaybackState == MpvPlayerState.Idle || Player.PlaybackState == MpvPlayerState.End)
        {
            return;
        }

        if (args.VirtualKey == Windows.System.VirtualKey.Right && !_isRightKeyDown && AppToolkit.NotModifierKeyPressed())
        {
            _isRightKeyDown = true;
            _isRightKeyTripleSpeed = false;
            _rightKeyLongPressTimer?.Start();
        }
    }

    private async void OnWindowKeyUp(InputKeyboardSource sender, KeyEventArgs args)
    {
        if (IsPopupVisible || IsExtraPanelVisible)
        {
            return;
        }

        if (args.VirtualKey == Windows.System.VirtualKey.Escape)
        {
            BackToDefaultModeCommand.Execute(default);
        }

        if (Player is null || Player.PlaybackState == MpvPlayerState.Idle || Player.PlaybackState == MpvPlayerState.End)
        {
            return;
        }

        if (IsStatsOverlayShown)
        {
            if (args.VirtualKey is Windows.System.VirtualKey.Up or
                Windows.System.VirtualKey.Down or
                Windows.System.VirtualKey.Number1 or
                Windows.System.VirtualKey.Number2 or
                Windows.System.VirtualKey.Number3 or
                Windows.System.VirtualKey.Number4 or
                Windows.System.VirtualKey.Number5)
            {
                await Client!.SendKeyPressAsync(args.VirtualKey.ToString().Replace("Number", string.Empty, StringComparison.Ordinal).ToLowerInvariant());
                return;
            }
        }

        if (args.VirtualKey == Windows.System.VirtualKey.Space)
        {
            PlayPauseCommand.Execute(default);
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.Right)
        {
            _rightKeyLongPressTimer?.Stop();
            if (_isRightKeyTripleSpeed)
            {
                _isRightKeyTripleSpeed = false;
                await ApplySpeedAsync(_lastSpeed);
                LastSpeedChangingTime = DateTimeOffset.Now;
                IsSpeedChanging = true;
            }
            else
            {
                if (AppToolkit.IsOnlyCtrlPressed())
                {
                    IncreaseSpeedCommand.Execute(default);
                }
                else if (AppToolkit.IsOnlyShiftPressed())
                {
                    if (IsNextButtonEnabled)
                    {
                        PlayNextCommand.Execute(default);
                    }
                }
                else if (!IsControlsVisible && AppToolkit.NotModifierKeyPressed())
                {
                    SkipForwardCommand.Execute(default);
                }
            }

            _isRightKeyDown = false;
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.Left)
        {
            if (AppToolkit.IsOnlyCtrlPressed())
            {
                DecreaseSpeedCommand.Execute(default);
            }
            else if (AppToolkit.IsOnlyShiftPressed())
            {
                if (IsPrevButtonEnabled)
                {
                    PlayPreviousCommand.Execute(default);
                }
            }
            else if (!IsControlsVisible && AppToolkit.NotModifierKeyPressed())
            {
                SkipBackwardCommand.Execute(default);
            }
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.Up)
        {
            if (AppToolkit.IsOnlyCtrlPressed())
            {
                IncreaseSpeedCommand.Execute(default);
            }
            else if (!IsControlsVisible && AppToolkit.NotModifierKeyPressed())
            {
                IncreaseVolumeCommand.Execute(default);
            }
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.Down)
        {
            if (AppToolkit.IsOnlyCtrlPressed())
            {
                DecreaseSpeedCommand.Execute(default);
            }
            else if (!IsControlsVisible && AppToolkit.NotModifierKeyPressed())
            {
                DecreaseVolumeCommand.Execute(default);
            }
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.F11)
        {
            ToggleFullScreenCommand.Execute(default);
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.M && AppToolkit.IsOnlyCtrlPressed())
        {
            ToggleCompactOverlayCommand.Execute(default);
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.PageUp)
        {
            SwitchToPreviousChapterCommand.Execute(default);
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.PageDown)
        {
            SwitchToNextChapterCommand.Execute(default);
        }
        else if (args.VirtualKey == Windows.System.VirtualKey.I)
        {
            ToggleStatsOverlayCommand.Execute(default);
        }
    }

    private async Task VerifyChaptersAsync()
    {
        if (Client is null || Player is null)
        {
            return;
        }

        var chapters = await Client.GetChaptersAsync();
        if (chapters.IsSuccess && Chapters.Count == 0 && chapters.Value.Count > 1)
        {
            var index = 0;
            foreach (var chapter in chapters.Value)
            {
                Chapters.Add(new ChapterItemViewModel(index, chapter.Title, chapter.Time));
                index++;
            }

            ChapterInitialized?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadSourcesAsync()
    {
        try
        {
            if (_sourceResolver is VideoMediaSourceResolver videoResolver)
            {
                logger.LogInformation("重载诊断：开始解析视频源");
                var sources = await videoResolver.GetSourcesAsync().WaitAsync(TimeSpan.FromSeconds(45));
                logger.LogInformation("重载诊断：视频源解析完成，共 {Count} 个", sources?.Count ?? 0);
                IsSourceSelectable = sources?.Count > 1;
                Sources.Clear();
                if (sources != null)
                {
                    var currentSource = videoResolver.GetCurrentFormat();
                    foreach (var source in sources)
                    {
                        var vm = new SourceItemViewModel(source, SelectSourceAsync);
                        if (currentSource != null)
                        {
                            vm.IsSelected = source.Quality == currentSource.Quality;
                        }

                        Sources.Add(vm);
                    }

                    SelectedSource = Sources.FirstOrDefault(p => p.IsSelected);
                    UpdateActualQualityText();
                }

                var c = ((VideoConnectorViewModel)Connector);
                DanmakuSend.ResetData(c._view.Information.Identifier.Id, c._part.Identifier.Id);
                logger.LogInformation("重载诊断：开始初始化下载器");
                c.InitializeDownloader(sources);
                logger.LogInformation("重载诊断：下载器初始化完成");
            }
            else if (_sourceResolver is PgcMediaSourceResolver pgcResolver)
            {
                var sources = await pgcResolver.GetSourcesAsync().WaitAsync(TimeSpan.FromSeconds(45));
                IsSourceSelectable = sources?.Count > 1;
                Sources.Clear();
                if (sources != null)
                {
                    var currentSource = pgcResolver.GetCurrentFormat();
                    foreach (var source in sources)
                    {
                        var vm = new SourceItemViewModel(source, SelectSourceAsync);
                        if (currentSource != null)
                        {
                            vm.IsSelected = source.Quality == currentSource.Quality;
                        }

                        Sources.Add(vm);
                    }

                    SelectedSource = Sources.FirstOrDefault(p => p.IsSelected);
                    UpdateActualQualityText();
                }

                var c = ((PgcConnectorViewModel)Connector);
                var aid = c._episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Aid).ToString();
                var cid = c._episode.GetExtensionIfNotNull<long>(EpisodeExtensionDataId.Cid).ToString();
                DanmakuSend.ResetData(aid.ToString(), cid.ToString());
                c.InitializeDownloader(sources);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载清晰度列表失败");
        }
    }

    private async Task LoadSubtitlesAsync()
    {
        try
        {
            if (!IsSubtitleEnabled)
            {
                return;
            }

            var service = this.Get<ISubtitleService>();
            if (Connector is VideoConnectorViewModel videoVM)
            {
                var aid = videoVM._view.Information.Identifier.Id;
                var cid = videoVM._part.Identifier.Id;
                // 字幕元数据这一步原来没超时：B 站 player/v2 偶发不返回时 InitializeAsync 会一直等，
                // 界面就永远停在"连接中"（从结束页切新视频时实测过）。给个上限，超时按没有字幕处理.
                logger.LogInformation("开始加载字幕元数据：aid={Aid} cid={Cid}", aid, cid);
                var metas = await service.GetSubtitleMetasAsync(aid, cid).WaitAsync(TimeSpan.FromSeconds(10));
                logger.LogInformation("字幕元数据返回 {Count} 条", metas?.Count ?? 0);
                Subtitles.Clear();
                if (metas?.Count > 0)
                {
                    foreach (var item in metas)
                    {
                        var vm = new SubtitleItemViewModel(item, SelectSubtitleAsync, DeselectSubtitleAsync);
                        Subtitles.Add(vm);
                    }
                }

                if (Subtitles.Count > 0 && Subtitles.Any(p => !p.IsAI))
                {
                    var firstNonAi = Subtitles.First(p => !p.IsAI);
                    firstNonAi.SelectCommand.Execute(default);
                }

                IsSubtitleEmpty = Subtitles.Count == 0;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载字幕列表失败");
        }
    }

    private async Task SelectSubtitleAsync(SubtitleItemViewModel vm)
    {
        foreach (var item in Subtitles)
        {
            item.IsSelected = item.Data.Equals(vm.Data);
        }

        if (string.IsNullOrEmpty(vm._srtFilePath))
        {
            return;
        }

        await Client!.SetExternalSubtitleTrackAsync(vm._srtFilePath);
    }

    private async Task DeselectSubtitleAsync(SubtitleItemViewModel vm)
    {
        foreach (var item in Subtitles)
        {
            item.IsSelected = false;
        }

        await Client!.SetSubtitleTrackAsync(default);
    }

    private static string[] FindAnime4KShaders(Anime4KMode mode)
    {
        if (mode == Anime4KMode.None)
        {
            return [];
        }

        var assetsFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "Anime4K");
        if (mode == Anime4KMode.LowA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowB)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowC)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Upscale_Denoise_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowAA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl"), GetShaderPath("Anime4K_Restore_CNN_S.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowBB)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_S.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.LowCA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Upscale_Denoise_CNN_x2_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Restore_CNN_S.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_S.glsl")];
        }
        else if (mode == Anime4KMode.HighA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_VL.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighB)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_VL.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighC)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Upscale_Denoise_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighAA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_VL.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_Restore_CNN_M.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighBB)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_VL.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Restore_CNN_Soft_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }
        else if (mode == Anime4KMode.HighCA)
        {
            return [GetShaderPath("Anime4K_Clamp_Highlights.glsl"), GetShaderPath("Anime4K_Upscale_Denoise_CNN_x2_VL.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x2.glsl"), GetShaderPath("Anime4K_AutoDownscalePre_x4.glsl"), GetShaderPath("Anime4K_Restore_CNN_M.glsl"), GetShaderPath("Anime4K_Upscale_CNN_x2_M.glsl")];
        }

        return [];

        string GetShaderPath(string shaderName) => Path.Combine(assetsFolder, shaderName);
    }

    private static string[] FindArtCNNShaders(ArtCNNMode mode)
    {
        if (mode == ArtCNNMode.None)
        {
            return [];
        }

        var assetsFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "ArtCNN");
        if (mode == ArtCNNMode.C4F16)
        {
            return [GetShaderPath("ArtCNN_C4F16.glsl")];
        }
        else if (mode == ArtCNNMode.C4F16DN)
        {
            return [GetShaderPath("ArtCNN_C4F16_DN.glsl")];
        }
        else if (mode == ArtCNNMode.C4F16DS)
        {
            return [GetShaderPath("ArtCNN_C4F16_DS.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32)
        {
            return [GetShaderPath("ArtCNN_C4F32.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32DN)
        {
            return [GetShaderPath("ArtCNN_C4F32_DN.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32DS)
        {
            return [GetShaderPath("ArtCNN_C4F32_DS.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32Ani4KI2)
        {
            return [GetShaderPath("Ani4Kv2_ArtCNN_C4F32_i2.glsl")];
        }
        else if (mode == ArtCNNMode.C4F32AniSDI4)
        {
            return [GetShaderPath("AniSD_ArtCNN_C4F32_i4.glsl")];
        }

        return [];
        string GetShaderPath(string shaderName) => Path.Combine(assetsFolder, shaderName);
    }

    private static string[] FindNnedi3Shaders(Nnedi3Mode mode)
    {
        if (mode == Nnedi3Mode.None)
        {
            return [];
        }

        var assetsFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "nnedi3");
        var igvFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "igv");

        if (mode == Nnedi3Mode.Basic)
        {
            return [GetShaderPath("nnedi3-nns32-win8x4.glsl"), GetIgvShaderPath("adaptive-sharpen_luma.glsl")];
        }
        else if (mode == Nnedi3Mode.Plus)
        {
            return [GetShaderPath("nnedi3-nns64-win8x4.glsl"), GetIgvShaderPath("adaptive-sharpen_luma.glsl")];
        }
        else if (mode == Nnedi3Mode.Pro)
        {
            return [GetShaderPath("nnedi3-nns128-win8x4.glsl"), GetIgvShaderPath("adaptive-sharpen_luma.glsl")];
        }
        else if (mode == Nnedi3Mode.ProMax)
        {
            return [GetShaderPath("nnedi3-nns256-win8x4.glsl"), GetIgvShaderPath("adaptive-sharpen_luma.glsl")];
        }

        return [];

        string GetShaderPath(string shaderName) => Path.Combine(assetsFolder, shaderName);
        string GetIgvShaderPath(string shaderName) => Path.Combine(igvFolder, shaderName);
    }

    private static string[] FindRavuShaders(RavuMode mode)
    {
        if (mode == RavuMode.None)
        {
            return [];
        }

        var assetsFolder = Path.Combine(Package.Current.InstalledPath, "Assets", "Ravu");

        if (mode == RavuMode.LiteArR2)
        {
            return [GetShaderPath("ravu-lite-ar-r2.hook")];
        }
        else if (mode == RavuMode.LiteArR3)
        {
            return [GetShaderPath("ravu-lite-ar-r2.hook")];
        }
        else if (mode == RavuMode.LiteArR4)
        {
            return [GetShaderPath("ravu-lite-ar-r4.hook")];
        }
        else if (mode == RavuMode.LiteR2)
        {
            return [GetShaderPath("ravu-lite-r2.hook")];
        }
        else if (mode == RavuMode.LiteR3)
        {
            return [GetShaderPath("ravu-lite-r3.hook")];
        }
        else if (mode == RavuMode.LiteR4)
        {
            return [GetShaderPath("ravu-lite-r4.hook")];
        }

        return [];

        string GetShaderPath(string shaderName) => Path.Combine(assetsFolder, shaderName);
    }

    private string[] GetCombinedShaders()
    {
        var shaders = new List<string>();
        shaders.AddRange(FindAnime4KShaders(Anime4KMode));
        shaders.AddRange(FindArtCNNShaders(ArtCNNMode));
        shaders.AddRange(FindRavuShaders(RavuMode));
        shaders.AddRange(FindNnedi3Shaders(Nnedi3Mode));
        return [.. shaders.Distinct()];
    }

    private void CheckClearShaderEnabled()
    {
        IsClearShaderEnabled = Anime4KMode != Anime4KMode.None ||
                                 ArtCNNMode != ArtCNNMode.None ||
                                 RavuMode != RavuMode.None ||
                                 Nnedi3Mode != Nnedi3Mode.None;
    }
}











