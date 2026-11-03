// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using Richasy.MpvKernel.Core.Enums;
using Richasy.MpvKernel.Player;

namespace BiliCopilot.UI.Models;

/// <summary>
/// 播放器外观状态.
/// 把 mpv 控制器与网页播放器的状态统一成同一份对外接口，
/// 这样播放界面只需要读这里，不必关心底下跑的是哪个引擎.
/// </summary>
public sealed partial class PlayerSurface : INotifyPropertyChanged
{
    private readonly MpvPlayer? _mpv;
    private readonly Controls.Core.WebVideoPlayer? _web;

    private double _webPosition;
    private double _webDuration = -1;
    private double _webVolume = 100;
    private double _webRate = 1;
    private bool _webInitialized;
    private bool _webFullScreen;
    private bool _webCompactOverlay;
    private MpvPlayerState _webState = MpvPlayerState.Idle;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerSurface"/> class.
    /// </summary>
    public PlayerSurface(MpvPlayer mpv)
    {
        _mpv = mpv;
        _mpv.PropertyChanged += OnMpvPropertyChanged;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerSurface"/> class.
    /// </summary>
    public PlayerSurface(Controls.Core.WebVideoPlayer web)
    {
        _web = web;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 当前是否使用网页播放器.
    /// </summary>
    public bool IsWebEngine => _web is not null;

    /// <summary>
    /// 底层 mpv 控制器（网页模式为 null）.
    /// </summary>
    public MpvPlayer? Mpv => _mpv;

    /// <summary>
    /// 播放位置（秒）.
    /// </summary>
    public double Position => _web is not null ? _webPosition : _mpv!.Position;

    /// <summary>
    /// 媒体时长（秒）.
    /// </summary>
    public double Duration => _web is not null ? Math.Max(0, _webDuration) : _mpv!.Duration;

    /// <summary>
    /// 播放状态.
    /// </summary>
    public MpvPlayerState PlaybackState => _web is not null ? _webState : _mpv!.PlaybackState;

    /// <summary>
    /// 音量（0-100）.
    /// </summary>
    public double Volume => _web is not null ? _webVolume : _mpv!.Volume;

    /// <summary>
    /// 播放速度.
    /// </summary>
    public double PlaybackRate => _web is not null ? _webRate : _mpv!.PlaybackRate;

    /// <summary>
    /// 是否正在加载.
    /// </summary>
    public bool IsLoading => _web is not null
        ? _webState is MpvPlayerState.Buffering or MpvPlayerState.Idle
        : _mpv!.IsLoading;

    /// <summary>
    /// 是否正在播放.
    /// </summary>
    public bool IsPlaying => _web is not null
        ? _webState == MpvPlayerState.Playing
        : _mpv!.IsPlaying;

    /// <summary>
    /// 是否已经停止.
    /// </summary>
    public bool IsStopped => _web is not null
        ? _webState is MpvPlayerState.Idle or MpvPlayerState.End
        : _mpv!.IsStopped;

    /// <summary>
    /// 播放器是否已就绪.
    /// </summary>
    public bool IsPlaybackInitialized => _web is not null ? _webInitialized : _mpv!.IsPlaybackInitialized;

    /// <summary>
    /// 是否全屏.
    /// </summary>
    public bool IsFullScreen => _web is not null ? _webFullScreen : _mpv!.IsFullScreen;

    /// <summary>
    /// 是否进入紧凑覆盖层.
    /// </summary>
    public bool IsCompactOverlay => _web is not null ? _webCompactOverlay : _mpv!.IsCompactOverlay;

    /// <summary>
    /// 媒体标题.
    /// </summary>
    public string Title => _mpv?.Title ?? string.Empty;

    /// <summary>
    /// 缓存速度.
    /// </summary>
    public double CacheSpeed => _mpv?.CacheSpeed ?? 0;

    /// <summary>
    /// 通知属性变化.
    /// </summary>
    public void RaisePropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// 初始化播放器.
    /// </summary>
    public Task InitializeAsync() => _mpv?.InitializeAsync() ?? Task.CompletedTask;

    /// <summary>
    /// 更新媒体源与历史记录处理器.
    /// </summary>
    internal void UpdateResolvers(Resolvers.MediaSourceResolverBase source, IMpvMediaHistoryResolver? history)
        => _mpv?.UpdateResolvers(source, history);

    /// <summary>
    /// 重播.
    /// </summary>
    public Task ReplayAsync() => _mpv?.ReplayAsync() ?? Task.CompletedTask;

    /// <summary>
    /// 从指定位置重播.
    /// </summary>
    public Task ReplayAsync(double position) => _mpv?.ReplayAsync(position) ?? Task.CompletedTask;

    /// <summary>
    /// 释放资源.
    /// </summary>
    public ValueTask DisposeAsync() => _mpv?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>
    /// 网页模式下更新播放位置.
    /// </summary>
    internal void UpdateWebPosition(double position)
    {
        _webPosition = position;
        RaisePropertyChanged(nameof(Position));
    }

    /// <summary>
    /// 网页模式下更新时长.
    /// </summary>
    internal void UpdateWebDuration(double duration)
    {
        _webDuration = duration;
        RaisePropertyChanged(nameof(Duration));
    }

    /// <summary>
    /// 网页模式下更新播放状态.
    /// </summary>
    internal void UpdateWebState(MpvPlayerState state)
    {
        _webState = state;
        _webInitialized = state is not MpvPlayerState.Idle;
        RaisePropertyChanged(nameof(PlaybackState));
        RaisePropertyChanged(nameof(IsPlaying));
        RaisePropertyChanged(nameof(IsStopped));
        RaisePropertyChanged(nameof(IsLoading));
        RaisePropertyChanged(nameof(IsPlaybackInitialized));
    }

    /// <summary>
    /// 网页模式下更新音量.
    /// </summary>
    internal void UpdateWebVolume(double volume)
    {
        _webVolume = volume;
        RaisePropertyChanged(nameof(Volume));
    }

    /// <summary>
    /// 网页模式下更新速度.
    /// </summary>
    internal void UpdateWebRate(double rate)
    {
        _webRate = rate;
        RaisePropertyChanged(nameof(PlaybackRate));
    }

    /// <summary>
    /// 网页模式下更新全屏状态.
    /// </summary>
    internal void UpdateWebFullScreen(bool isFullScreen)
    {
        _webFullScreen = isFullScreen;
        RaisePropertyChanged(nameof(IsFullScreen));
    }

    /// <summary>
    /// 网页模式下更新紧凑覆盖层状态.
    /// </summary>
    internal void UpdateWebCompactOverlay(bool isCompactOverlay)
    {
        _webCompactOverlay = isCompactOverlay;
        RaisePropertyChanged(nameof(IsCompactOverlay));
    }

    /// <summary>
    /// 网页模式下重置位置与状态.
    /// </summary>
    internal void ResetWebPlayback()
    {
        _webPosition = 0;
        _webDuration = -1;
        _webState = MpvPlayerState.Buffering;
        RaisePropertyChanged(nameof(Position));
        RaisePropertyChanged(nameof(Duration));
        RaisePropertyChanged(nameof(PlaybackState));
    }

    private void OnMpvPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => PropertyChanged?.Invoke(this, e);
}