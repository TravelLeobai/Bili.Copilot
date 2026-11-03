// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Core;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.MpvKernel.Core.Enums;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型：网页播放引擎相关逻辑.
/// </summary>
public sealed partial class PlayerViewModel
{
    private WebVideoPlayer? _webPlayer;

    /// <summary>
    /// 是否使用网页播放器.
    /// </summary>
    internal bool IsWebEngine
        => SettingsToolkit.ReadLocalSetting(SettingNames.PlayerEngine, PlayerEngineType.Mpv) == PlayerEngineType.Web;

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