// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.MpvKernel.Core.Enums;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型：网页播放引擎相关逻辑.
/// </summary>
public sealed partial class PlayerViewModel
{
    private WebVideoPlayer? _webPlayer;
    private bool _forceWebFallback;
    private bool _mpvInitFailed;
    private bool _isFallbackInProgress;

    /// <summary>
    /// 是否使用网页播放器.
    /// </summary>
    internal bool IsWebEngine
        => SettingsToolkit.ReadLocalSetting(SettingNames.PlayerEngine, PlayerEngineType.Mpv) == PlayerEngineType.Web;

    /// <summary>
    /// 实际是否走网页播放器（含 mpv 失败后的自动回落）.
    /// </summary>
    internal bool UseWebEngine => IsWebEngine || _forceWebFallback;

    /// <summary>
    /// 播放失败后回落到网页播放器（Shaka）。
    /// </summary>
    /// <param name="resumePosition">要续播的位置，小于 1 秒视为从头.</param>
    internal async Task<bool> TryFallbackToWebEngineAsync(double resumePosition = 0)
    {
        if (IsWebEngine || _forceWebFallback || _isFallbackInProgress)
        {
            return false;
        }

        if (!SettingsToolkit.ReadLocalSetting(SettingNames.PlayerAutoFallback, true))
        {
            return false;
        }

        if (Window is null || Window.IsClosed || _sourceResolver is null)
        {
            return false;
        }

        _isFallbackInProgress = true;
        try
        {
            _webPlayer ??= CreateWebPlayer();
            Window.SetVideoHost(_webPlayer);

            var source = await _sourceResolver.GetSourceAsync();
            var videoUrl = source?.Url;
            var audioUrl = source?.Options?.AudioTracks?.FirstOrDefault();
            if (string.IsNullOrEmpty(videoUrl) && string.IsNullOrEmpty(audioUrl))
            {
                logger.LogWarning("回落到网页播放器失败：没有可用的音视频地址");
                return false;
            }

            _webPlayer.CookieString = this.Get<IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;

            if (Player is not null)
            {
                Player.PropertyChanged -= OnPlayerPropertyChanged;
            }

            Player = new PlayerSurface(_webPlayer);
            Player.PropertyChanged += OnPlayerPropertyChanged;
            _forceWebFallback = true;

            var volume = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerVolume, 100d);
            var rate = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerSpeed, 1d);
            Player.UpdateWebVolume(volume);
            Player.UpdateWebRate(rate);

            await _webPlayer.LoadAsync(videoUrl, audioUrl);
            await _webPlayer.SetVolumeAsync(volume);
            await _webPlayer.SetRateAsync(rate);

            if (resumePosition > 1)
            {
                await _webPlayer.SeekAsync(resumePosition);
            }

            logger.LogInformation("mpv 播放失败，已回落到网页播放器，位置 {Position:F1} 秒", resumePosition);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "回落到网页播放器失败");
            _forceWebFallback = false;
            return false;
        }
        finally
        {
            _isFallbackInProgress = false;
        }
    }

    /// <summary>
    /// 用网页播放器初始化播放界面.
    /// </summary>
    internal async Task InitializeWebEngineAsync()
    {
        if (Window is null || Window.IsClosed)
        {
            return;
        }

        _webPlayer ??= CreateWebPlayer();
        Window.SetVideoHost(_webPlayer);

        if (Player is null || !Player.IsWebEngine)
        {
            Player = new PlayerSurface(_webPlayer);
            Player.PropertyChanged += OnPlayerPropertyChanged;
        }

        await _webPlayer.LoadAsync(null, null);
    }

    /// <summary>
    /// 用网页播放器载入当前媒体的音视频地址.
    /// </summary>
    internal async Task LoadWebMediaAsync()
    {
        if (_webPlayer is null || _sourceResolver is null)
        {
            return;
        }

        var source = await _sourceResolver.GetSourceAsync();
        var videoUrl = source?.Url;
        var audioUrl = source?.Options?.AudioTracks?.FirstOrDefault();
        if (string.IsNullOrEmpty(videoUrl) && string.IsNullOrEmpty(audioUrl))
        {
            ErrorMessage = "没有拿到可用的播放地址.";
            return;
        }

        _webPlayer.CookieString = this.Get<IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;
        Player?.ResetWebPlayback();
        var volume = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerVolume, 100d);
        var speed = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerSpeed, 1d);
        Player?.UpdateWebVolume(volume);
        Player?.UpdateWebRate(speed);
        await _webPlayer.LoadAsync(videoUrl, audioUrl);
        await _webPlayer.SetVolumeAsync(volume);
        await _webPlayer.SetRateAsync(speed);
    }

    /// <summary>
    /// 停止网页播放.
    /// </summary>
    internal void StopWebPlayback()
    {
        if (_webPlayer is null)
        {
            return;
        }

        _ = _webPlayer.PauseAsync();
        Player?.UpdateWebState(MpvPlayerState.Idle);
    }

    private WebVideoPlayer CreateWebPlayer()
    {
        var player = new WebVideoPlayer();
        player.PositionChanged += OnWebPositionChanged;
        player.MediaLoaded += OnWebMediaLoaded;
        player.MediaEnded += OnWebMediaEnded;
        player.PlaybackStateChanged += OnWebPlaybackStateChanged;
        player.PlaybackFailed += OnWebPlaybackFailed;
        player.EngineReady += OnWebEngineReady;
        return player;
    }

    private void OnWebPositionChanged(object? sender, double position)
    {
        Player?.UpdateWebPosition(position);
    }

    private void OnWebMediaLoaded(object? sender, double duration)
    {
        Player?.UpdateWebDuration(duration);
        Player?.UpdateWebState(MpvPlayerState.Playing);
    }

    private void OnWebMediaEnded(object? sender, EventArgs e)
    {
        Player?.UpdateWebState(MpvPlayerState.End);
        IsEnd = true;
    }

    private void OnWebPlaybackStateChanged(object? sender, string state)
    {
        var mapped = state switch
        {
            "playing" => MpvPlayerState.Playing,
            "paused" => MpvPlayerState.Paused,
            "buffering" => MpvPlayerState.Buffering,
            "end" => MpvPlayerState.End,
            _ => Player?.PlaybackState ?? MpvPlayerState.Idle,
        };

        Player?.UpdateWebState(mapped);
    }

    private void OnWebEngineReady(object? sender, string info)
        => logger.LogInformation("网页播放器就绪：{Info}", info);

    private void OnWebPlaybackFailed(object? sender, string message)
    {
        ErrorMessage = message;
    }

    private async Task ApplyVolumeAsync(double volume)
    {
        if (IsWebEngine && _webPlayer is not null)
        {
            Player?.UpdateWebVolume(volume);
            await _webPlayer.SetVolumeAsync(volume);
            return;
        }

        await Client!.SetVolumeAsync(volume);
    }

    private async Task ApplySpeedAsync(double speed)
    {
        if (IsWebEngine && _webPlayer is not null)
        {
            Player?.UpdateWebRate(speed);
            await _webPlayer.SetRateAsync(speed);
            return;
        }

        await Client!.SetSpeedAsync(speed);
    }

    private async Task ApplySeekAsync(double position)
    {
        if (IsWebEngine && _webPlayer is not null)
        {
            await _webPlayer.SeekAsync(position);
            return;
        }

        await Client!.SetCurrentPositionAsync(position);
    }

    private async Task ApplyPauseAsync()
    {
        if (IsWebEngine && _webPlayer is not null)
        {
            await _webPlayer.PauseAsync();
            return;
        }

        await Client!.PauseAsync();
    }

    private async Task ApplyResumeAsync()
    {
        if (IsWebEngine && _webPlayer is not null)
        {
            await _webPlayer.ResumeAsync();
            return;
        }

        await Client!.ResumeAsync();
    }

    private async Task ApplyFullScreenAsync(bool isFullScreen)
    {
        if (IsWebEngine)
        {
            Player?.UpdateWebFullScreen(isFullScreen);
            if (isFullScreen)
            {
                Window?.EnterFullScreen();
            }
            else
            {
                Window?.ExitFullScreen();
            }

            return;
        }

        await Client!.SetFullScreenStateAsync(isFullScreen);
    }

    private async Task ApplyCompactOverlayAsync(bool isCompactOverlay)
    {
        if (IsWebEngine)
        {
            Player?.UpdateWebCompactOverlay(isCompactOverlay);
            if (isCompactOverlay)
            {
                Window?.EnterCompactOverlay();
            }
            else
            {
                Window?.ExitCompactOverlay();
            }

            return;
        }

        await Client!.SetCompactOverlayStateAsync(isCompactOverlay);
    }
}