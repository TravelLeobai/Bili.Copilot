// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

internal sealed partial class VideoMediaSourceResolver(IPlayerService playerService, IBiliCookiesResolver cookiesResolver, ILogger<VideoMediaSourceResolver> logger) : MediaSourceResolverBase
{
    private MediaSnapshot _snapshot;
    private VideoPart _part;
    private TaskCompletionSource<bool>? _initTask;
    private CancellationTokenSource? _initCts;
    private DashMediaInformation? _playbackInfo;

    public void Initialize(MediaSnapshot snapshot, VideoPart part)
    {
        _snapshot = snapshot;
        _part = part;
        _playbackInfo = default;
        _initTask?.TrySetCanceled();
        _initCts?.Cancel();
    }

    internal void ResetSnapshot(MediaSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    internal PlayerFormatInformation? GetCurrentFormat()
    {
        var preferFormatSetting = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PreferQuality, PreferQualityType.Auto);
        var availableFormats = _playbackInfo.Formats.ToList().ConvertAll(p => new SourceItemViewModel(p, default));
        if (_snapshot.Video.Publisher?.User?.Id != this.Get<AccountViewModel>().MyProfile.User.Id)
        {
            availableFormats = [.. availableFormats.Where(p => p.IsEnabled)];
        }

        // 画质解锁开启且勾了「自动最高画质」时，直接选当前可用的最高一档.
        if (QualityUnlockService.IsEnabled && QualityUnlockService.AutoMaxQuality && availableFormats.Count > 0)
        {
            var maxFormat = availableFormats.OrderByDescending(p => p.Data.Quality).First();
            this.Get<ILogger<VideoMediaSourceResolver>>().LogInformation(
                "画质解锁：自动选中最高画质 qn={Quality}",
                maxFormat.Data.Quality);
            return maxFormat.Data;
        }

        SourceItemViewModel? selectedFormat = default;
        if (_snapshot.PreferQuality > 0)
        {
            selectedFormat = availableFormats.Find(p => p.Data.Quality == _snapshot.PreferQuality);
        }

        if (selectedFormat == null)
        {
            if (preferFormatSetting == PreferQualityType.Auto)
            {
                var lastSelectedFormat = SettingsToolkit.ReadLocalSetting(SettingNames.LastSelectedVideoQuality, 0);
                selectedFormat = availableFormats.Find(p => p.Data.Quality == lastSelectedFormat);
            }
            else if (preferFormatSetting == PreferQualityType.UHD)
            {
                selectedFormat = availableFormats.Find(p => p.Data.Quality == 120);
            }
            else if (preferFormatSetting == PreferQualityType.HD)
            {
                var hdFormats = availableFormats.Where(p => p.Data.Quality == 116 || p.Data.Quality == 80).ToList();
                selectedFormat = hdFormats.OrderByDescending(p => p.Data.Quality).FirstOrDefault();
            }
        }

        if (selectedFormat is null)
        {
            var maxQuality = availableFormats.Max(p => p.Data.Quality);
            selectedFormat = availableFormats.Find(p => p.Data.Quality == maxQuality);
        }

        return selectedFormat.Data;
    }

    public override IMpvMediaSourceResolver Clone() => throw new NotImplementedException();

    public override async Task<MpvMediaSource> GetSourceAsync()
    {
        var volume = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PlayerVolume, 100d);
        var speed = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PlayerSpeed, 1d);
        var cookies = cookiesResolver.GetCookieString();
        const string referer = InternalHttpExtensions.VideoReferer;
        const string userAgent = InternalHttpExtensions.VideoUserAgent;
        var headers = new Dictionary<string, string>
        {
            { "Cookie", cookies },
            { "Referer", referer },
        };
        var options = new MpvPlayOptions
        {
            WindowHandle = WindowHandle,
            InitialVolume = volume,
            InitialSpeed = speed,
            UserAgent = userAgent,
            HttpHeaders = headers,
            EnableCookies = true,
            MediaName = _snapshot.Video.Identifier.Title,
        };

        await LoadMediaInfoAsync();
        var (videoUrl, audioUrl) = await GetPlayUrlsAsync();
        var fileUrl = string.Empty;
        if (!string.IsNullOrEmpty(videoUrl))
        {
            fileUrl = videoUrl;
            if (!string.IsNullOrEmpty(audioUrl))
            {
                options.AudioTracks ??= [];
                options.AudioTracks.Add(audioUrl);
            }
        }
        else if (!string.IsNullOrEmpty(audioUrl))
        {
            fileUrl = audioUrl;
        }

        return new MpvMediaSource(fileUrl, _snapshot.Video.Identifier.Id, _snapshot.Video.Identifier.Title, options);
    }

    public async Task<List<PlayerFormatInformation>?> GetSourcesAsync()
    {
        await LoadMediaInfoAsync();
        return [.. _playbackInfo?.Formats];
    }

    internal async Task LoadMediaInfoAsync()
    {
        if (_snapshot is null || _part is null)
        {
            throw new InvalidOperationException("必须先初始化媒体信息.");
        }

        if (_playbackInfo is not null)
        {
            return;
        }

        if (_initTask?.Task.IsCompleted == true)
        {
            return;
        }

        if (_initTask is not null)
        {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            await _initTask.Task;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
            return;
        }

        _initTask = new TaskCompletionSource<bool>();
        _initCts = new CancellationTokenSource();
        try
        {
            _playbackInfo = await playerService.GetVideoPlayDetailAsync(_snapshot.Video.Identifier, Convert.ToInt64(_part.Identifier.Id), _initCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"获取 {_snapshot.Video.Identifier.Title} 播放信息失败");
            _initTask.TrySetException(new InvalidOperationException("获取 Dash 播放信息失败."));
            return;
        }

        _initTask.TrySetResult(true);
    }

    private async Task<(string? video, string? audio)> GetPlayUrlsAsync()
    {
        var videoSegments = _playbackInfo!.Videos;
        var audioSegments = _playbackInfo.Audios;
        var selectedFormat = GetCurrentFormat();
        if (selectedFormat is null)
        {
            throw new InvalidCastException("无法获取到有效的播放地址");
        }

        var maxAudioQuality = audioSegments?.Max(p => Convert.ToInt32(p.Id));
        var preferCodec = AppToolkit.GetPreferCodecId();
        var vSeg = videoSegments?.FirstOrDefault(p => p.Id == selectedFormat.Quality.ToString() && p.Codecs.Contains(preferCodec))
            ?? videoSegments?.FirstOrDefault(p => p.Id == selectedFormat.Quality.ToString());
        var aSeg = audioSegments?.FirstOrDefault(p => p.Id == maxAudioQuality.ToString());

        const string userAgent = InternalHttpExtensions.VideoUserAgent;
        const string referer = InternalHttpExtensions.VideoReferer;

        // 启用了画质解锁时，先让第三方服务器代取 playurl（可拿到高画质 dash 地址）.
        if (QualityUnlockService.IsEnabled)
        {
            var cookie = cookiesResolver.GetCookieString();
            var csrf = ExtractCookie(cookie, "bili_jct");
            var mid = this.Get<AccountViewModel>().MyProfile?.User?.Id ?? string.Empty;
            var targetQuality = QualityUnlockService.AutoMaxQuality
                ? Math.Max(selectedFormat.Quality, 127)
                : selectedFormat.Quality;

            var unlocked = await QualityUnlockService.GetPlayUrlsAsync(
                _snapshot.Video.Identifier.Id,
                _part.Identifier.Id,
                targetQuality,
                cookie,
                csrf,
                mid,
                logger);

            if (unlocked.Success)
            {
                var unlockedVideo = string.IsNullOrEmpty(unlocked.Video) ? null : CdnAccelerator.Rewrite(unlocked.Video!, true);
                var unlockedAudio = string.IsNullOrEmpty(unlocked.Audio) ? null : CdnAccelerator.Rewrite(unlocked.Audio!, true);
                SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedVideoQuality, unlocked.Quality > 0 ? unlocked.Quality : selectedFormat.Quality);
                return (unlockedVideo, unlockedAudio);
            }

            logger.LogInformation("画质解锁不可用（{Message}），改用常规地址", unlocked.Message);
        }

        // 启用了播放代理时，先尝试从解析服务器拿地址（可解锁区域限制）.
        if (ProxiedPlayUrlService.IsEnabled)
        {
            var cookie = cookiesResolver.GetCookieString();
            var proxied = await ProxiedPlayUrlService.GetPlayUrlsAsync(
                _snapshot.Video.Identifier.Id,
                _part.Identifier.Id,
                selectedFormat.Quality,
                preferCodec,
                cookie,
                logger);

            if (!string.IsNullOrEmpty(proxied?.Video) || !string.IsNullOrEmpty(proxied?.Audio))
            {
                var proxiedVideo = proxied!.Value.Video is null
                    ? null
                    : CdnAccelerator.Rewrite(proxied.Value.Video, true);
                var proxiedAudio = proxied.Value.Audio is null
                    ? null
                    : CdnAccelerator.Rewrite(proxied.Value.Audio, true);

                SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedVideoQuality, selectedFormat.Quality);
                return (CdnAccelerator.SpeedUp(proxiedVideo, userAgent), CdnAccelerator.SpeedUp(proxiedAudio, userAgent));
            }
        }

        var videoUrl = vSeg is null
            ? null
            : await PlayUrlOptimizer.PickPlayableUrlAsync(vSeg.BaseUrl, vSeg.BackupUrls, userAgent, referer, logger);
        var audioUrl = aSeg is null
            ? null
            : await PlayUrlOptimizer.PickPlayableUrlAsync(aSeg.BaseUrl, aSeg.BackupUrls, userAgent, referer, logger);

        SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedVideoQuality, selectedFormat.Quality);
        return (CdnAccelerator.SpeedUp(videoUrl, userAgent), CdnAccelerator.SpeedUp(audioUrl, userAgent));
    }

    private static string ExtractCookie(string cookie, string name)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0 && part[..index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(index + 1)..];
            }
        }

        return string.Empty;
    }
}
