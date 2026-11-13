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
    private string _lastUnlockSummary = "未尝试";
    private string _lastUnlockDiagnostic = string.Empty;
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

    /// <summary>
    /// 这次播放有没有走第三方服务器解锁画质，给统计面板显示.
    /// </summary>
    internal string LastUnlockSummary
    {
        get => _lastUnlockSummary;
        private set => _lastUnlockSummary = value;
    }

    /// <summary>
    /// 解锁失败时的诊断信息（成功或没尝试时为空）.
    /// </summary>
    internal string LastUnlockDiagnostic
    {
        get => _lastUnlockDiagnostic;
        private set => _lastUnlockDiagnostic = value;
    }
    // 第三方服务器解锁回来的真实档位与轨参数：统计面板按这个显示，
    // 内核 dash 里非会员最高 80，直接读它会「解锁 116 / 画质 80」自相矛盾.
    private int _unlockedQuality;
    private int _unlockedWidth;
    private int _unlockedHeight;
    private long _unlockedBandwidth;
    private string? _unlockedCodecs;


    /// <summary>
    /// 实际生效的清晰度 qn：走过第三方解锁就用解锁回来的那档，
    /// 否则取当前格式的 qn。顶栏画质标签按它显示，避免"显示 1080P、实际 1080P60".
    /// </summary>
    internal int ActualQuality => _unlockedQuality > 0 ? _unlockedQuality : (GetCurrentFormat()?.Quality ?? 0);

    internal PlayerFormatInformation? GetCurrentFormat()
    {
        var preferFormatSetting = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PreferQuality, PreferQualityType.Auto);
        var availableFormats = _playbackInfo.Formats.ToList().ConvertAll(p => new SourceItemViewModel(p, default));
        if (_snapshot.Video.Publisher?.User?.Id != this.Get<AccountViewModel>().MyProfile.User.Id)
        {
            availableFormats = [.. availableFormats.Where(p => p.IsEnabled)];
        }

        // 开了画质解锁就按「画质偏好」直接挑一档：
        //   自动最高 → 当前可用最高；
        //   1080P 60 帧 → 116，没有就退 112（高码率），再没有才退最高可用.
        if (QualityUnlockService.IsEnabled && availableFormats.Count > 0)
        {
            var targetQuality = QualityUnlockService.PickPreferredQuality(
                availableFormats.Select(p => p.Data.Quality));
            if (targetQuality > 0)
            {
                var preferred = availableFormats.FirstOrDefault(p => p.Data.Quality == targetQuality)
                    ?? availableFormats.OrderByDescending(p => p.Data.Quality).First();
                this.Get<ILogger<VideoMediaSourceResolver>>().LogInformation(
                    "画质解锁：按偏好选 qn={Quality}（偏好={Preference}）",
                    preferred.Data.Quality,
                    QualityUnlockService.Preference);
                return preferred.Data;
            }
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

    /// <summary>
    /// 生成统计信息浮层要显示的字段（点播视频）.
    /// 数据都来自 dash 段本身：MimeType / Codecs / 宽高 / 带宽 / 地址都能直接读到.
    /// </summary>
    internal IReadOnlyList<PlayerStatisticItem> BuildStatistics()
    {
        var items = new List<PlayerStatisticItem>();
        if (_playbackInfo is null)
        {
            items.Add(new("状态", "还没拿到播放信息"));
            return items;
        }

        var videos = _playbackInfo.Videos?.ToList() ?? [];
        var audios = _playbackInfo.Audios?.ToList() ?? [];
        var format = GetCurrentFormat();
        var video = videos.FirstOrDefault(p => format is not null && p.Id == format.Quality.ToString())
            ?? videos.OrderByDescending(p => SafeInt(p.Id)).FirstOrDefault();
        var audio = audios.OrderByDescending(p => SafeInt(p.Id)).FirstOrDefault();

        items.Add(new(
            "Mime Type",
            $"{video?.MimeType}; codecs=\"{video?.Codecs}\", {audio?.MimeType}; codecs=\"{audio?.Codecs}\""));
        items.Add(new("播放内核", "mpv"));
        items.Add(new("高画质解锁", LastUnlockSummary));
        if (!string.IsNullOrEmpty(LastUnlockDiagnostic))
        {
            items.Add(new("解锁失败原因", LastUnlockDiagnostic));
        }
        if (_unlockedQuality > 0)
        {
            // 解锁成功：展示第三方那份 dash 的真实参数（分辨率/码率/编码）.
            items.Add(new("分辨率", $"{_unlockedWidth} × {_unlockedHeight}"));
            items.Add(new("视频码率（标称）", $"{_unlockedBandwidth / 1000} Kbps [{DescribeCodec(_unlockedCodecs)}]"));
        }
        else if (video is not null)
        {
            items.Add(new("分辨率", $"{video.Width} × {video.Height}"));
            items.Add(new("视频码率（标称）", $"{video.Bandwidth / 1000} Kbps [{DescribeCodec(video.Codecs)}]"));
        }

        if (audio is not null)
        {
            items.Add(new("音频码率（标称）", $"{audio.Bandwidth / 1000} Kbps [{DescribeCodec(audio.Codecs)}]"));
        }

        items.Add(new("分段", $"视频 {videos.Count} 段 / 音频 {audios.Count} 段"));
        items.Add(new("视频宿主机", ReadHost(video?.BaseUrl)));
        items.Add(new("音频宿主机", ReadHost(audio?.BaseUrl)));
        items.Add(new("画质", _unlockedQuality > 0 ? $"qn={_unlockedQuality}（第三方）" : format is null ? "未知" : $"qn={format.Quality}"));
        return items;
    }

    private static int SafeInt(string? value)
        => int.TryParse(value, out var result) ? result : 0;

    private static string DescribeCodec(string? codecs)
    {
        if (string.IsNullOrEmpty(codecs))
        {
            return "未知";
        }

        var text = codecs.ToLowerInvariant();
        if (text.Contains("hev") || text.Contains("hvc") || text.Contains("h265"))
        {
            return "HEVC";
        }

        if (text.Contains("av01") || text.Contains("av1"))
        {
            return "AV1";
        }

        if (text.Contains("avc") || text.Contains("h264"))
        {
            return "AVC";
        }

        return text.Contains("mp4a") ? "AAC" : codecs;
    }

    private static string ReadHost(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "—";

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
        LastUnlockSummary = QualityUnlockService.IsEnabled ? "未尝试" : "否（设置里没开画质解锁）";
        _unlockedQuality = 0;
        _unlockedWidth = 0;
        _unlockedHeight = 0;
        _unlockedBandwidth = 0;
        _unlockedCodecs = null;
        if (QualityUnlockService.IsEnabled)
        {
            var cookie = cookiesResolver.GetCookieString();
            var csrf = ExtractCookie(cookie, "bili_jct");
            var mid = this.Get<AccountViewModel>().MyProfile?.User?.Id ?? string.Empty;
            // 档位要按「片源真实上限」来试：直接要 120(4K) 遇到只有 1080P 的片源，
            // 服务端上游会失败并回 {"code":2,"message":"fail"}（实测：1080P 片源要 4K 必挂）。
            // 所以从高到低逐个试，成功即停；每失败一次会消耗一次解析额度，最多试 3 档.
            // 按设置里的画质偏好决定要哪几档（自动最高 / 1080P60 优先 / 1080P 高码率优先）.
            var ladder = QualityUnlockService.GetQualityLadder(selectedFormat.Quality);
            QualityUnlockResult unlocked = default;
            // 整条阶梯要有时间预算：mpv 初始化在等这个地址，超过 20 秒就会被判超时、
            // 整轮播放被放弃（现象就是点视频后白屏、连接中半天不动）。
            // 实测单次解锁请求 4~6 秒（含服务端取 playurl），三次阶梯轻松破 20 秒，
            // 所以给 8 秒预算，超了就直接用普通地址，画质解锁下次再说.
            var unlockBudget = TimeSpan.FromSeconds(8);
            var unlockStart = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var candidate in ladder.Distinct())
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(unlockStart) > unlockBudget)
                {
                    logger.LogWarning(
                        "画质解锁：阶梯预算 {Budget}s 用完，改用普通播放地址（中断于 qn={Candidate}）",
                        unlockBudget.TotalSeconds,
                        candidate);
                    break;
                }

                unlocked = await QualityUnlockService.GetPlayUrlsAsync(
                    _snapshot.Video.Identifier.Id,
                    _part.Identifier.Id,
                    candidate,
                    cookie,
                    csrf,
                    mid,
                    logger);
                if (unlocked.Success)
                {
                    break;
                }
            }

            if (unlocked.Success)
            {
                // 先按客户端既有规则优选，再把 cn-gdfs/upos 之类换到能连通的镜像，
                // 否则 mpv 会在「Failed to open ... Timeout was reached」上挂死.
                // 解锁分支原来直接返回，绕过了 PlayUrlOptimizer，导致「自动优选」在开着画质解锁时
                // 根本不生效。这里先按样本实测一轮候选节点，Rewrite 自然就会选到最快的那台.
                if (CdnAccelerator.IsEnabled && !string.IsNullOrEmpty(unlocked.Video))
                {
                    await CdnAccelerator.RankHostsAsync(unlocked.Video!, logger);
                }

                var unlockedVideo = string.IsNullOrEmpty(unlocked.Video)
                    ? null
                    : QualityUnlockService.PreferMirrorHost(CdnAccelerator.Rewrite(unlocked.Video!, true));
                var unlockedAudio = string.IsNullOrEmpty(unlocked.Audio)
                    ? null
                    : QualityUnlockService.PreferMirrorHost(CdnAccelerator.Rewrite(unlocked.Audio!, true));
                SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedVideoQuality, unlocked.Quality > 0 ? unlocked.Quality : selectedFormat.Quality);
                LastUnlockSummary = $"是（第三方服务器，qn={(unlocked.Quality > 0 ? unlocked.Quality : selectedFormat.Quality)}）";
                _unlockedQuality = unlocked.Quality > 0 ? unlocked.Quality : selectedFormat.Quality;
                _unlockedWidth = unlocked.Width;
                _unlockedHeight = unlocked.Height;
                _unlockedBandwidth = unlocked.Bandwidth;
                _unlockedCodecs = unlocked.Codecs;
                LastUnlockDiagnostic = string.Empty;
                return (unlockedVideo, unlockedAudio);
            }

            LastUnlockSummary = $"否（{unlocked.Message}）";
            LastUnlockDiagnostic = unlocked.Diagnostic ?? string.Empty;
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
                if (CdnAccelerator.IsEnabled && !string.IsNullOrEmpty(proxied.Value.Video))
                {
                    await CdnAccelerator.RankHostsAsync(proxied.Value.Video!, logger);
                }

                var proxiedVideo = proxied!.Value.Video is null
                    ? null
                    : CdnAccelerator.Rewrite(proxied.Value.Video, true);
                var proxiedAudio = proxied.Value.Audio is null
                    ? null
                    : CdnAccelerator.Rewrite(proxied.Value.Audio, true);

                SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedVideoQuality, selectedFormat.Quality);
                return (proxiedVideo, proxiedAudio);
            }
        }

        var videoUrl = vSeg is null
            ? null
            : await PlayUrlOptimizer.PickPlayableUrlAsync(vSeg.BaseUrl, vSeg.BackupUrls, userAgent, referer, logger);
        var audioUrl = aSeg is null
            ? null
            : await PlayUrlOptimizer.PickPlayableUrlAsync(aSeg.BaseUrl, aSeg.BackupUrls, userAgent, referer, logger);

        SettingsToolkit.WriteLocalSetting(SettingNames.LastSelectedVideoQuality, selectedFormat.Quality);
        return (videoUrl, audioUrl);
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




