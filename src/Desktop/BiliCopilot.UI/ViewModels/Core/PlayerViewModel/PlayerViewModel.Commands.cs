// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Danmaku;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型.
/// </summary>
public sealed partial class PlayerViewModel
{
    [RelayCommand]
    private void Back()
    {
        UseIntegrationOperation = false;
        Window?.Close();
    }

    [RelayCommand]
    private void ToggleTopMost()
    {
        IsTopMost = !IsTopMost;
        Window?.SetTopMost(IsTopMost);
    }

    [RelayCommand]
    private async Task IncreaseVolumeAsync()
    {
        if (Player is null)
        {
            return;
        }

        var newVolume = Math.Min(MaxVolume, Player.Volume + 5);
        IsVolumeChanging = true;
        LastVolumeChangingTime = DateTimeOffset.Now;
        CurrentVolume = newVolume;

        if (Math.Abs(newVolume - Player.Volume) >= 1)
        {
            await ApplyVolumeAsync(newVolume);
        }
    }

    [RelayCommand]
    private async Task DecreaseVolumeAsync()
    {
        if (Player is null)
        {
            return;
        }

        var newVolume = Math.Max(0, Player.Volume - 5);
        IsVolumeChanging = true;
        LastVolumeChangingTime = DateTimeOffset.Now;
        CurrentVolume = newVolume;

        if (Math.Abs(newVolume - Player.Volume) >= 1)
        {
            await ApplyVolumeAsync(newVolume);
        }
    }

    [RelayCommand]
    private async Task IncreaseSpeedAsync()
    {
        if (Player is null)
        {
            return;
        }

        var newSpeed = Math.Abs(Player.PlaybackRate - 0.1) < 0.01 ? 0.25 : Math.Min(3, Player.PlaybackRate + 0.25);
        if (Math.Abs(newSpeed - Player.PlaybackRate) > 0.01)
        {
            IsSpeedChanging = true;
            LastSpeedChangingTime = DateTimeOffset.Now;
            await ApplySpeedAsync(newSpeed);
        }
    }

    [RelayCommand]
    private async Task DecreaseSpeedAsync()
    {
        if (Player is null)
        {
            return;
        }

        var newSpeed = Math.Max(0.1, Player.PlaybackRate - 0.25);
        if (Math.Abs(newSpeed - Player.PlaybackRate) > 0.01)
        {
            IsSpeedChanging = true;
            LastSpeedChangingTime = DateTimeOffset.Now;
            await ApplySpeedAsync(newSpeed);
        }
    }

    [RelayCommand]
    private async Task BackToDefaultModeAsync()
    {
        if (Player is null)
        {
            return;
        }

        if ((IsConnecting || Player.IsLoading) && Window is not null)
        {
            var presenterKind = Window.GetWindow().Presenter.Kind;
            if (presenterKind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
            {
                Window.ExitFullScreen();
            }
            else if (presenterKind == Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay)
            {
                Window.ExitCompactOverlay();
            }
        }

        if (IsExtraPanelVisible)
        {
            IsExtraPanelVisible = false;
            return;
        }

        if (Player.IsFullScreen)
        {
            await ApplyFullScreenAsync(false);
        }
        else if (Player.IsCompactOverlay)
        {
            await ApplyCompactOverlayAsync(false);
        }
    }

    [RelayCommand]
    private Task SkipBackwardAsync()
    {
        var backwardSeconds = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.StepBackwardSecond, 10d);
        IsProgressChanging = true;
        LastProgressChangingTime = DateTimeOffset.Now;
        return ChangePositionAsync(Math.Max(Player.Position - backwardSeconds, 0));
    }

    [RelayCommand]
    private Task SkipForwardAsync()
    {
        var forwardSeconds = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.StepForwardSecond, 30d);
        IsProgressChanging = true;
        LastProgressChangingTime = DateTimeOffset.Now;
        return ChangePositionAsync(Math.Min(Player.Position + forwardSeconds, Player.Duration - 1));
    }

    /// <summary>
    /// 改变当前播放位置.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task ChangePositionAsync(double position)
    {
        if (Player is null || position < 0 || position > Player.Duration || Math.Abs(Player.Position - position) < 2)
        {
            return;
        }

        await ApplySeekAsync(position);
    }

    /// <summary>
    /// 改变音量.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    public async Task ChangeVolumeAsync(double volume)
    {
        if (Player is null || volume < 0)
        {
            return;
        }

        IsVolumeChanging = true;
        LastVolumeChangingTime = DateTimeOffset.Now;
        CurrentVolume = volume;

        if (Math.Abs(Player.Volume - volume) >= 1)
        {
            await ApplyVolumeAsync(volume);
        }
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (Player is null || Player.PlaybackState == MpvPlayerState.Idle)
        {
            return;
        }

        if (Player.PlaybackState == MpvPlayerState.Playing)
        {
            await ApplyPauseAsync();
        }
        else if (Player.IsStopped)
        {
            await Player.ReplayAsync();
        }
        else
        {
            await ApplyResumeAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleFullScreenAsync()
    {
        if (Player is null)
        {
            return;
        }

        IsExtraPanelVisible = false;
        await ApplyFullScreenAsync(!Player.IsFullScreen);
    }

    [RelayCommand]
    private async Task ToggleCompactOverlayAsync()
    {
        if (Player is null)
        {
            return;
        }

        IsExtraPanelVisible = false;
        await ApplyCompactOverlayAsync(!Player.IsCompactOverlay);
    }

    [RelayCommand]
    private async Task PlayNextAsync()
    {
        await ApplyPauseAsync();
        await Client!.StopAsync();
        await Task.Delay(500);
        if (IsConnecting || (DateTimeOffset.Now - _lastPlayNextOrPrevTime) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastPlayNextOrPrevTime = DateTimeOffset.Now;
        await Connector!.PlayNextAsync();
    }

    [RelayCommand]
    private async Task PlayPreviousAsync()
    {
        await ApplyPauseAsync();
        await Client!.StopAsync();
        await Task.Delay(500);
        if (IsConnecting || (DateTimeOffset.Now - _lastPlayNextOrPrevTime) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastPlayNextOrPrevTime = DateTimeOffset.Now;
        await Connector!.PlayPreviousAsync();
    }

    [RelayCommand]
    private async Task TripleSpeedAsync()
    {
        if (Player is null || !Player.IsPlaying)
        {
            return;
        }

        _lastSpeed = Player.PlaybackRate;
        IsHoldingSpeedChanging = true;
        await ApplySpeedAsync(SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.TempPlaybackRate, 3d));
        LastSpeedChangingTime = DateTimeOffset.Now;
        IsSpeedChanging = true;
        _tripleTimer!.Start();
    }

    [RelayCommand]
    private async Task RestoreSpeedAsync()
    {
        if (Player is null)
        {
            return;
        }

        IsHoldingSpeedChanging = false;
        _tripleTimer!.Stop();
        if (Math.Abs(_lastSpeed - Player.PlaybackRate) > 0.01)
        {
            await ApplySpeedAsync(_lastSpeed);
        }
    }

    [RelayCommand]
    private async Task ResetSubtitlePositionAsync()
    {
        if (Client != null && Player != null)
        {
            SubtitlePosition = IsVerticalScreen ? _verticalSubtitlePosition : _horizontalSubtitlePosition;
            await Client.SetSubtitlePositionAsync(Convert.ToInt32(SubtitlePosition));
        }
    }

    [RelayCommand]
    private async Task ChangeSubtitlePositionAsync(double position)
    {
        if (Client != null && Player != null && Math.Abs(SubtitlePosition - position) >= 1)
        {
            if (IsVerticalScreen)
            {
                _verticalSubtitlePosition = position;
            }
            else
            {
                _horizontalSubtitlePosition = position;
            }

            SubtitlePosition = position;
            await Client.SetSubtitlePositionAsync(Convert.ToInt32(position));
        }
    }

    [RelayCommand]
    private async Task ReloadFontsAsync()
    {
        if (Fonts.Count > 0)
        {
            return;
        }

        var fonts = await this.Get<IFontToolkit>().GetFontsAsync();
        foreach (var item in fonts)
        {
            Fonts.Add(item);
        }

        var localFont = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.SubtitleFontFamily, "Segoe UI");
        if (!Fonts.Select(p => p.LocalName).Contains(localFont))
        {
            localFont = "Segoe UI";
        }

        ChangeSubtitleFontFamilyCommand.Execute(localFont);
    }

    [RelayCommand]
    private async Task ChangeSubtitleFontSizeAsync(double fontSize)
    {
        if (Client != null && Player != null && Math.Abs(SubtitleFontSize - fontSize) >= 1)
        {
            SubtitleFontSize = fontSize;
            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.SubtitleFontSize, fontSize);
            await Client.SetSubtitleFontSizeAsync(Convert.ToInt32(fontSize));
        }
    }

    [RelayCommand]
    private async Task ChangeSubtitleFontFamilyAsync(string font)
    {
        if (Client != null && Player != null && !string.IsNullOrEmpty(font) && SubtitleFontFamily != font && Window?.IsClosed == false)
        {
            SubtitleFontFamily = font;
            SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.SubtitleFontFamily, font);
            await Client.SetSubtitleFontFamilyAsync(font);
        }
    }

    [RelayCommand]
    private async Task SwitchToPreviousChapterAsync()
    {
        if (Player is null || Player.PlaybackState == MpvPlayerState.Idle)
        {
            return;
        }

        var lastSelectedChapter = Chapters.LastOrDefault(p => p.IsPlayed && !p.IsPlaying);
        if (lastSelectedChapter is null)
        {
            return;
        }

        await ApplySeekAsync(lastSelectedChapter.Position);
    }

    [RelayCommand]
    private async Task SwitchToNextChapterAsync()
    {
        if (Player is null || Player.PlaybackState == MpvPlayerState.Idle)
        {
            return;
        }

        var nextSelectedChapter = Chapters.FirstOrDefault(p => !p.IsPlayed && !p.IsPlaying);
        if (nextSelectedChapter is null)
        {
            return;
        }

        await ApplySeekAsync(nextSelectedChapter.Position);
    }

    [RelayCommand]
    private async Task SendKeyAsync(string key)
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            await Client!.SendKeyPressAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to send key: {key}");
        }
    }

    [RelayCommand]
    private async Task ToggleStatsOverlayAsync()
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            await Client!.ToggleStatsOverlayAsync();
            IsStatsOverlayShown = !IsStatsOverlayShown;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to toggle stats overlay.");
        }
    }

    [RelayCommand]
    private async Task SetSubtitleDelaySecondsAsync(double seconds)
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            await Client!.SetSubtitleDelaySecondsAsync(seconds);
            SubtitleDelaySeconds = seconds;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set subtitle delay failed");
        }
    }

    [RelayCommand]
    private async Task ToggleSubtitleEnabledAsync()
    {
        if (IsSubtitleEnabled)
        {
            var selected = Subtitles.FirstOrDefault(p => p.IsSelected);
            if (selected != null)
            {
                Subtitles.FirstOrDefault(p => p.IsSelected)?.SelectCommand.Execute(default);
            }
        }
        else
        {
            await Client!.SetSubtitleTrackAsync(default);
        }
    }

    [RelayCommand]
    private void HideNextTip()
        => RequestHideNextTip?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task TogglePanscanAsync()
    {
        var v = UsingPanscan ? 0 : 1;
        UsingPanscan = !UsingPanscan;
        try
        {
            await Client!.SetPanscanAsync(v);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set panscan failed");
        }
    }

    [RelayCommand]
    private async Task ToggleMuteStateAsync()
    {
        if (Player is null || Client is null)
        {
            return;
        }

        IsMute = !IsMute;
        await Client.SetMuteAsync(IsMute);
    }

    [RelayCommand]
    private async Task CopyPlayUrlAsync()
    {
        if (_sourceResolver is null)
        {
            return;
        }

        var source = await _sourceResolver!.GetSourceAsync();
        if (!string.IsNullOrEmpty(source.Url))
        {
            var dp = new DataPackage();
            dp.SetText(source.Url);
            Clipboard.SetContent(dp);
            Window?.ShowTip(ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.Copied), InfoType.Success);
        }
    }

    [RelayCommand]
    private async Task TakeScreenshotAsync()
    {
        if (Player?.IsPlaybackInitialized != true)
        {
            return;
        }

        try
        {
            var fileName = DateTimeOffset.Now.ToString("yyyy-MM-dd-HH-mm-ss") + ".png";
            var customScreenshotFolderPath = SettingsToolkit.ReadLocalSetting(SettingNames.CustomScreenshotFolderPath, string.Empty);
            if (string.IsNullOrEmpty(customScreenshotFolderPath) || !Directory.Exists(customScreenshotFolderPath))
            {
                customScreenshotFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Bili screenshots");
            }

            if (!Directory.Exists(customScreenshotFolderPath))
            {
                Directory.CreateDirectory(customScreenshotFolderPath);
            }

            var path = Path.Combine(customScreenshotFolderPath, fileName);
            await Client!.TakeScreenshotAsync(path);
            if (File.Exists(path))
            {
                var action = SettingsToolkit.ReadLocalSetting(SettingNames.ScreenshotAction, ScreenshotAction.Open);
                if (action == ScreenshotAction.None)
                {
                    return;
                }

                var file = await StorageFile.GetFileFromPathAsync(path);
                if (action == ScreenshotAction.Copy)
                {
                    var dp = new DataPackage();
                    dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                    Clipboard.SetContent(dp);
                    Window?.ShowTip(ResourceToolkit.GetLocalizedString(StringNames.Copied), InfoType.Success);
                }
                else if (action == ScreenshotAction.Open)
                {
                    await Launcher.LaunchFileAsync(file).AsTask();
                }
            }
        }
        catch (Exception)
        {

            throw;
        }
    }

    [RelayCommand]
    private async Task SendDanmakuAsync()
    {
        if (Player is null || IsConnecting || IsDanmakuLoading)
        {
            return;
        }

        try
        {
            await ApplyPauseAsync();
            var dialog = new DanmakuSendDialog(DanmakuSend, () => IsPopupVisible = true)
            {
                XamlRoot = Window!.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var input = dialog.GetInputText();
                if (!string.IsNullOrEmpty(input))
                {
                    await DanmakuSend.SendDanmakuAsync(input, Convert.ToInt32(Player.Position));
                    Danmaku.AddDanmaku(input);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Send danmaku failed");
            Window.ShowTip(ex.Message, InfoType.Error);
        }

        IsPopupVisible = false;
        await ApplyResumeAsync();
    }

    [RelayCommand]
    private async Task ChangeAnime4KModeAsync(Anime4KMode mode)
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            Anime4KMode = mode;
            PersistEnhancement(SettingNames.Anime4KMode, mode);
            await ApplyShadersAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set Anime4K mode failed");
        }
    }

    [RelayCommand]
    private async Task ChangeArtCNNModeAsync(ArtCNNMode mode)
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            ArtCNNMode = mode;
            PersistEnhancement(SettingNames.ArtCNNMode, mode);
            await ApplyShadersAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set ArtCNN mode failed");
        }
    }

    [RelayCommand]
    private async Task ChangeNnedi3ModeAsync(Nnedi3Mode mode)
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            Nnedi3Mode = mode;
            PersistEnhancement(SettingNames.Nnedi3Mode, mode);
            await ApplyShadersAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set NNEDI3 mode failed");
        }
    }

    [RelayCommand]
    private async Task ChangeRavuModeAsync(RavuMode mode)
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            RavuMode = mode;
            PersistEnhancement(SettingNames.RavuMode, mode);
            await ApplyShadersAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set RAVU mode failed");
        }
    }

    [RelayCommand]
    private async Task ChangeNvidiaVsrModeAsync(VsrScale scale)
        => await ApplyVsrAsync(scale);
    private async Task ApplyVsrAsync(VsrScale scale)
    {
        if (Player is null || Client is null)
        {
            return;
        }

        VsrScale = scale;
        PersistEnhancement(SettingNames.NvidiaVsrScale, scale);

        if (scale != VsrScale.None && !HasNvidiaGpu)
        {
            VsrScale = VsrScale.None;
            SettingsToolkit.WriteLocalSetting(SettingNames.NvidiaVsrScale, VsrScale.None);
            Window?.ShowTip("当前设备没有检测到 NVIDIA 显卡，无法开启超分辨率", InfoType.Warning);
            return;
        }

        var wasPlaying = Player.IsPlaying && Player.IsPlaybackInitialized;
        try
        {
            if (wasPlaying)
            {
                await ApplyPauseAsync();
            }

            var scaleNumber = scale switch
            {
                VsrScale.x1_5 => 1.5,
                VsrScale.x2_0 => 2.0,
                VsrScale.x3_0 => 3.0,
                VsrScale.x4_0 => 4.0,
                _ => 1.0,
            };

            // 只切换滤镜和超分，不在播放期间重建 GPU 上下文，否则播放窗口会白屏.
            await Client.SetNvidiaVsrAsync(scale != VsrScale.None, scaleNumber);
            if (scale == VsrScale.None)
            {
                await RestoreDecodeSettingsAsync();
            }

            if (wasPlaying)
            {
                await ApplyResumeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Set Nvidia VSR scale failed");
            await RecoverPlaybackEnhancementsAsync();
        }
    }

    private async Task RestoreDecodeSettingsAsync()
    {
        if (Client is null)
        {
            return;
        }

        var decodeType = SettingsToolkit.ReadLocalSetting(SettingNames.PreferDecode, PreferDecodeType.Auto);
        if (decodeType == PreferDecodeType.Auto)
        {
            await Client.SetGpuContextAsync(GpuContextType.Auto);
            await Client.SetHardwareDecodeAsync(HardwareDecodeType.Auto);
        }
        else if (decodeType == PreferDecodeType.D3D11)
        {
            await Client.SetGpuContextAsync(GpuContextType.D3D11);
            await Client.SetHardwareDecodeAsync(HardwareDecodeType.D3D11va);
        }
        else if (decodeType == PreferDecodeType.D3D12)
        {
            await Client.SetGpuContextAsync(GpuContextType.D3D11);
            await Client.SetHardwareDecodeAsync(HardwareDecodeType.D3D12vaCopy);
        }
        else if (decodeType == PreferDecodeType.NVDEC)
        {
            await Client.SetGpuContextAsync(GpuContextType.D3D11);
            await Client.SetHardwareDecodeAsync(HardwareDecodeType.Nvdec);
        }
        else if (decodeType == PreferDecodeType.Vulkan)
        {
            await Client.SetGpuContextAsync(GpuContextType.WindowsVulkan);
            await Client.SetHardwareDecodeAsync(HardwareDecodeType.Vulkan);
        }
        else if (decodeType == PreferDecodeType.Software)
        {
            await Client.SetHardwareDecodeAsync(HardwareDecodeType.None);
        }
    }

    internal async Task RecoverPlaybackEnhancementsAsync()
    {
        if (Client is null)
        {
            return;
        }

        var hasEnhancement = VsrScale != VsrScale.None
            || Anime4KMode != Anime4KMode.None
            || ArtCNNMode != ArtCNNMode.None
            || RavuMode != RavuMode.None
            || Nnedi3Mode != Nnedi3Mode.None;
        if (!hasEnhancement)
        {
            return;
        }

        try
        {
            // 只清当前会话，不动已保存的偏好，否则一次设备丢失就把用户的设置永久抹掉.
            Anime4KMode = Anime4KMode.None;
            ArtCNNMode = ArtCNNMode.None;
            RavuMode = RavuMode.None;
            Nnedi3Mode = Nnedi3Mode.None;
            VsrScale = VsrScale.None;
            IsEnhancementRecovered = true;
            CheckClearShaderEnabled();
            await Client.SetNvidiaVsrAsync(false);
            await Client.SetShadersAsync([]);
            await Client.SetVideoOutputAsync(VideoOutputType.GpuNext);
            await RestoreDecodeSettingsAsync();
            logger.LogWarning("本次播放已临时回退着色器与超分辨率（设备异常），保存的偏好仍然保留");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recover playback enhancements failed");
        }
    }

    /// <summary>
    /// 写播放增强设置；关闭「记住」时只清当前状态，不落盘.
    /// </summary>
    private void PersistEnhancement(BiliCopilot.UI.Models.Constants.SettingNames key, object value)
    {
        if (!SettingsToolkit.ReadLocalSetting(SettingNames.RememberPlaybackEnhancements, true))
        {
            return;
        }

        // 存字符串而不是枚举：SharedSettingsToolkit 内部走 JsonSerializer，裁剪构建里枚举可能写失败.
        SettingsToolkit.WriteLocalSetting(key, value.ToString() ?? string.Empty);
    }

    /// <summary>
    /// 读取播放增强设置（按字符串存，读不到就回落默认值）.
    /// </summary>
    private static T ReadEnhancement<T>(SettingNames key, T defaultValue)
        where T : struct, Enum
    {
        var raw = SettingsToolkit.ReadLocalSetting(key, string.Empty);
        return !string.IsNullOrEmpty(raw) && Enum.TryParse<T>(raw, true, out var value) ? value : defaultValue;
    }

    [RelayCommand]
    private async Task ClearShadersAsync()
    {
        if (Player is null)
        {
            return;
        }

        try
        {
            Anime4KMode = Anime4KMode.None;
            ArtCNNMode = ArtCNNMode.None;
            Nnedi3Mode = Nnedi3Mode.None;
            RavuMode = RavuMode.None;
            SettingsToolkit.WriteLocalSetting(SettingNames.Anime4KMode, Anime4KMode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.ArtCNNMode, ArtCNNMode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.Nnedi3Mode, Nnedi3Mode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.RavuMode, RavuMode.None);
            await ApplyShadersAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Clear shaders failed");
        }
    }

    private async Task ApplyShadersAsync()
    {
        if (Client is null || Player is null)
        {
            return;
        }

        var wasPlaying = Player.IsPlaying && Player.IsPlaybackInitialized;
        try
        {
            if (wasPlaying)
            {
                await ApplyPauseAsync();
            }

            await Client.SetShadersAsync(GetCombinedShaders());
            CheckClearShaderEnabled();

            if (wasPlaying)
            {
                await ApplyResumeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Apply shaders failed");
            await RecoverPlaybackEnhancementsAsync();
        }
    }
}