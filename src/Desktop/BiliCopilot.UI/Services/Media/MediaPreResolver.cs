// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Globalization;
using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 预解析结果：一张卡片点开前就能准备好的东西（cid、dash 信息、选好的地址、要登记的代理源）.
/// </summary>
internal sealed class MediaPreResolutionResult
{
    /// <summary>
    /// 视频 BV 号.
    /// </summary>
    public string VideoId { get; init; } = string.Empty;

    /// <summary>
    /// 分 P 的 cid.
    /// </summary>
    public string Cid { get; init; } = string.Empty;

    /// <summary>
    /// 标题.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// dash 播放信息（会同步进 <see cref="MediaPreResolutionCache"/>，播放侧点击时可直接复用）.
    /// </summary>
    public DashMediaInformation? PlaybackInfo { get; init; }

    /// <summary>
    /// 选中的画质 qn.
    /// </summary>
    public int Quality { get; init; }

    /// <summary>
    /// 视频源（未登记；由 <see cref="MediaPrefetchService.RegisterTarget"/> 登记成预取目标）.
    /// </summary>
    public MediaStreamSourceDescriptor? VideoSource { get; init; }

    /// <summary>
    /// 视频源的 dash 元信息.
    /// </summary>
    public MediaPrefetchSourceInfo VideoInfo { get; init; } = new();

    /// <summary>
    /// 音频源，可为 null（没有独立音频轨）.
    /// </summary>
    public MediaStreamSourceDescriptor? AudioSource { get; init; }

    /// <summary>
    /// 音频源的 dash 元信息.
    /// </summary>
    public MediaPrefetchSourceInfo AudioInfo { get; init; } = new();

    /// <summary>
    /// 媒体时长（秒），未知为 null.
    /// </summary>
    public double? DurationSeconds { get; init; }
}

/// <summary>
/// 「卡片 → cid → playurl」的结果缓存：预取算过的东西，点击播放时不必再算一遍。
/// <para>
/// 只缓存 dash 播放信息（含各轨地址），键是「bvid:cid」——换个分 P 就不能用别人的。
/// 有 TTL（默认 10 分钟，CDN 签名过期更快），而且取一次就删（播放侧只消费一次）。
/// </para>
/// </summary>
internal static class MediaPreResolutionCache
{
    private static readonly ConcurrentDictionary<string, (DashMediaInformation Info, long Stamped)> Entries = new(StringComparer.Ordinal);

    /// <summary>
    /// 缓存有效期（CDN 签名过期后地址就不能用了，别再喂给播放器）.
    /// </summary>
    public static TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 存一份预解析结果.
    /// </summary>
    /// <param name="videoId">视频 id.</param>
    /// <param name="cid">分 P cid.</param>
    /// <param name="info">dash 信息.</param>
    public static void Store(string videoId, string cid, DashMediaInformation info)
    {
        if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(cid) || info is null)
        {
            return;
        }

        Entries[BuildKey(videoId, cid)] = (info, Environment.TickCount64);
    }

    /// <summary>
    /// 取一份预解析结果（取走即删；过期会被丢掉）.
    /// </summary>
    /// <param name="videoId">视频 id.</param>
    /// <param name="cid">分 P cid.</param>
    /// <param name="info">dash 信息.</param>
    /// <returns>是否命中.</returns>
    public static bool TryTake(string videoId, string cid, out DashMediaInformation? info)
    {
        info = null;
        if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(cid))
        {
            return false;
        }

        var key = BuildKey(videoId, cid);
        if (!Entries.TryRemove(key, out var entry))
        {
            return false;
        }

        if (Environment.TickCount64 - entry.Stamped > Ttl.TotalMilliseconds)
        {
            return false;
        }

        info = entry.Info;
        return true;
    }

    /// <summary>
    /// 清空（诊断 / 测试用）.
    /// </summary>
    public static void Clear() => Entries.Clear();

    private static string BuildKey(string videoId, string cid)
        => videoId + ":" + cid;
}

/// <summary>
/// 预解析链路：把「卡片 → 分P/cid → playurl → 代理源」这两步串行请求提前做完。
/// <para>
/// 为什么值得单独抽出来：首帧慢的根因不是解码，而是点击后才开始
/// 「取视频信息（拿 cid）→ 取 playurl（拿签名地址）→ 建连拉首块」这条串行链。
/// 预取把前两步和「拉首块」都挪到点击之前，点击时只剩「打开播放器」。
/// </para>
/// <para>
/// 复用：段选择、身份构造、地址优选全部走 B/C 已有的
/// <see cref="MediaBufferProxyBridge.CreateRequest"/>、<see cref="MediaStreamIdentity"/>、
/// <see cref="PlayUrlOptimizer"/>，不另写一套规则，避免「预取的画质/地址和播放时的不是同一个」。
/// </para>
/// </summary>
internal sealed class MediaPreResolver
{
    private readonly IPlayerService _playerService;
    private readonly IBiliCookiesResolver _cookiesResolver;
    private readonly ILogger<MediaPreResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaPreResolver"/> class.
    /// </summary>
    /// <param name="playerService">播放服务.</param>
    /// <param name="cookiesResolver">Cookie 解析.</param>
    /// <param name="logger">日志.</param>
    public MediaPreResolver(
        IPlayerService playerService,
        IBiliCookiesResolver cookiesResolver,
        ILogger<MediaPreResolver> logger)
    {
        _playerService = playerService;
        _cookiesResolver = cookiesResolver;
        _logger = logger;
    }

    /// <summary>
    /// 解析一张卡片：拿 cid、拿 dash 信息、选好地址、拼出可登记的代理源.
    /// </summary>
    /// <param name="videoId">视频 BV 号.</param>
    /// <param name="title">标题（日志用，可为 null）.</param>
    /// <param name="ownerKey">归属键（页面级；预取登记会加上预取前缀）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>结果；无法预取（番剧 / 无音视频轨 / 请求失败）时返回 null.</returns>
    public async Task<MediaPreResolutionResult?> ResolveAsync(
        string videoId,
        string? title,
        string ownerKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(videoId))
        {
            return null;
        }

        var identifier = new MediaIdentifier(videoId, title ?? string.Empty, default);
        var view = await _playerService.GetVideoPageDetailAsync(identifier, cancellationToken).ConfigureAwait(false);
        var part = view?.Parts?.FirstOrDefault();
        if (part is null || !long.TryParse(part.Identifier.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cidValue))
        {
            _logger.LogDebug("预解析：{VideoId} 没有可用分P，跳过", videoId);
            return null;
        }

        var cid = part.Identifier.Id;
        var playbackInfo = await _playerService.GetVideoPlayDetailAsync(identifier, cidValue, cancellationToken).ConfigureAwait(false);
        if (playbackInfo?.Videos is null || playbackInfo.Videos.Count == 0)
        {
            _logger.LogDebug("预解析：{VideoId} 没有视频轨，跳过", videoId);
            return null;
        }

        var quality = ChooseQuality(playbackInfo);
        var preferCodec = AppToolkit.GetPreferCodecId();
        var displayName = string.IsNullOrEmpty(title)
            ? videoId
            : string.Create(CultureInfo.InvariantCulture, $"{title} · P{part.Index} · qn={quality}");

        const string userAgent = InternalHttpExtensions.VideoUserAgent;
        const string referer = InternalHttpExtensions.VideoReferer;
        var videoSeg = playbackInfo.Videos.FirstOrDefault(p => p.Id == quality.ToString(CultureInfo.InvariantCulture) && (p.Codecs?.Contains(preferCodec, StringComparison.Ordinal) ?? false))
            ?? playbackInfo.Videos.FirstOrDefault(p => p.Id == quality.ToString(CultureInfo.InvariantCulture))
            ?? playbackInfo.Videos.OrderByDescending(p => ParseInt(p.Id)).First();
        var audioSeg = playbackInfo.Audios?.OrderByDescending(p => ParseInt(p.Id)).FirstOrDefault();

        var videoUrl = await PlayUrlOptimizer
            .PickPlayableUrlAsync(videoSeg.BaseUrl, videoSeg.BackupUrls, userAgent, referer, _logger)
            .ConfigureAwait(false);
        var audioUrl = audioSeg is null
            ? null
            : await PlayUrlOptimizer
                .PickPlayableUrlAsync(audioSeg.BaseUrl, audioSeg.BackupUrls, userAgent, referer, _logger)
                .ConfigureAwait(false);

        // 段选择与身份构造复用 C 的门面：画质 / 编码 / init / index 的口径必须和播放时一致，
        // 否则预取缓存的 key 和播放时算出来的 key 不是同一个，全部白拉.
        var request = MediaBufferProxyBridge.CreateRequest(
            ownerKey: ownerKey,
            bvid: videoId,
            cid: cid,
            displayName: displayName,
            videoUrl: videoUrl,
            audioUrl: audioUrl,
            playbackInfo: playbackInfo,
            quality: quality,
            preferCodec: preferCodec);

        var cookie = _cookiesResolver.GetCookieString();
        var videoSource = new MediaStreamSourceDescriptor(request.VideoUrl, CreateIdentity(
            request.Bvid,
            request.Cid,
            request.QualityId,
            request.CodecId,
            request.MimeType,
            request.Bandwidth,
            request.Initialization,
            request.IndexRange,
            request.VideoUrl))
        {
            Cookie = cookie,
            Referer = referer,
            UserAgent = userAgent,
            DisplayName = displayName,
            OwnerKey = ownerKey,
        };

        var duration = part.Duration > 0
            ? part.Duration
            : playbackInfo.Duration > 0 ? playbackInfo.Duration : 0;
        var videoInfo = new MediaPrefetchSourceInfo
        {
            DisplayName = displayName,
            BandwidthBps = request.Bandwidth,
            InitializationRange = ParseRange(request.Initialization, out var init) ? init : null,
            IndexRange = ParseRange(request.IndexRange, out var index) ? index : null,
            DurationSeconds = duration > 0 ? duration : null,
        };

        MediaStreamSourceDescriptor? audioSource = null;
        var audioInfo = new MediaPrefetchSourceInfo { DisplayName = displayName + " · 音频" };
        if (!string.IsNullOrEmpty(request.AudioUrl) && audioSeg is not null)
        {
            audioSource = new MediaStreamSourceDescriptor(request.AudioUrl, CreateIdentity(
                request.Bvid,
                request.Cid,
                request.AudioQualityId ?? (request.QualityId + "-audio"),
                request.AudioCodecId,
                request.AudioMimeType ?? "audio/mp4",
                request.AudioBandwidth,
                null,
                null,
                request.AudioUrl))
            {
                Cookie = cookie,
                Referer = referer,
                UserAgent = userAgent,
                DisplayName = displayName + " · 音频",
                OwnerKey = ownerKey,
            };
            audioInfo.BandwidthBps = request.AudioBandwidth;
        }

        // 播放侧点击时可以直接用这份 dash 信息，省掉一次 playurl 请求.
        MediaPreResolutionCache.Store(videoId, cid, playbackInfo);

        _logger.LogInformation(
            "预解析：{Name} 分P={Cid} qn={Quality} 视频码率 {Bandwidth}kbps audio={HasAudio} 时长={Duration}s",
            displayName,
            cid,
            quality,
            request.Bandwidth / 1000,
            audioSource is not null,
            duration);

        return new MediaPreResolutionResult
        {
            VideoId = videoId,
            Cid = cid,
            Title = string.IsNullOrEmpty(title) ? videoId : title,
            PlaybackInfo = playbackInfo,
            Quality = quality,
            VideoSource = videoSource,
            VideoInfo = videoInfo,
            AudioSource = audioSource,
            AudioInfo = audioInfo,
            DurationSeconds = duration > 0 ? duration : null,
        };
    }

    /// <summary>
    /// 挑画质：和 <c>VideoMediaSourceResolver.GetCurrentFormat</c> 对「新快照」的判断保持一致。
    /// <para>
    /// 只在没有开启画质解锁时才会走到这里：开了画质解锁的场合，播放时会用第三方服务器
    /// 返回的更高档位，预取按普通 dash 拉的档位和它对不上（缓存 key 不同），不如不预取。
    /// </para>
    /// </summary>
    private int ChooseQuality(DashMediaInformation playbackInfo)
    {
        var isVip = this.Get<AccountViewModel>().MyProfile?.IsVip ?? false;
        var formats = playbackInfo.Formats?.ToList() ?? [];
        var usable = formats.Where(p => !p.NeedVip || isVip).ToList();
        if (usable.Count == 0)
        {
            usable = formats;
        }

        var available = usable.Select(p => p.Quality).Where(p => p > 0).Distinct().ToList();
        if (available.Count == 0)
        {
            return playbackInfo.Videos
                .Select(p => ParseInt(p.Id))
                .Where(p => p > 0)
                .DefaultIfEmpty(0)
                .Max();
        }

        var preference = SettingsToolkit.ReadLocalSetting(SettingNames.PreferQuality, PreferQualityType.Auto);
        if (preference == PreferQualityType.UHD && available.Contains(120))
        {
            return 120;
        }

        if (preference == PreferQualityType.HD)
        {
            if (available.Contains(116))
            {
                return 116;
            }

            if (available.Contains(80))
            {
                return 80;
            }
        }

        if (preference == PreferQualityType.Auto)
        {
            var last = SettingsToolkit.ReadLocalSetting(SettingNames.LastSelectedVideoQuality, 0);
            if (last > 0 && available.Contains(last))
            {
                return last;
            }
        }

        return available.Max();
    }

    private static MediaStreamIdentity CreateIdentity(
        string bvid,
        string cid,
        string qualityId,
        string? codecId,
        string? mimeType,
        long bandwidth,
        string? initialization,
        string? indexRange,
        string? url)
        => MediaStreamIdentity.Create(bvid, cid, qualityId, codecId, mimeType, bandwidth, initialization, indexRange, url);

    private static bool ParseRange(string? text, out MediaByteRange range)
        => MediaSegmentIndex.TryParseDashRange(text, out range);

    private static int ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
