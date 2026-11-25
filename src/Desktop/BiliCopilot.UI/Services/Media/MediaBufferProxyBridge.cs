// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 解析器要交给代理的一条媒体请求：原播放地址 + 用于构造逻辑身份的 dash 段信息.
/// </summary>
internal sealed class MediaProxySourceRequest
{
    /// <summary>
    /// 归属键：同一键的登记会被一起释放（换集 / 换视频时用）.
    /// </summary>
    public string OwnerKey { get; init; } = string.Empty;

    /// <summary>
    /// 视频 BV 号（番剧用 epId / seasonId）.
    /// </summary>
    public string Bvid { get; init; } = string.Empty;

    /// <summary>
    /// 分 P 的 cid.
    /// </summary>
    public string Cid { get; init; } = string.Empty;

    /// <summary>
    /// 画质 / 音轨 id.
    /// </summary>
    public string QualityId { get; init; } = string.Empty;

    /// <summary>
    /// 编码 id.
    /// </summary>
    public string? CodecId { get; init; }

    /// <summary>
    /// 视频 MimeType.
    /// </summary>
    public string MimeType { get; init; } = "video/mp4";

    /// <summary>
    /// 视频标称带宽.
    /// </summary>
    public long Bandwidth { get; init; }

    /// <summary>
    /// dash 段 Initialization.
    /// </summary>
    public string? Initialization { get; init; }

    /// <summary>
    /// dash 段 IndexRange.
    /// </summary>
    public string? IndexRange { get; init; }

    /// <summary>
    /// 视频播放地址（原地址，带签名）.
    /// </summary>
    public string VideoUrl { get; init; } = string.Empty;

    /// <summary>
    /// 音频播放地址，可为 null.
    /// </summary>
    public string? AudioUrl { get; init; }

    /// <summary>
    /// 音频轨 id.
    /// </summary>
    public string? AudioQualityId { get; init; }

    /// <summary>
    /// 音频 MimeType.
    /// </summary>
    public string? AudioMimeType { get; init; }

    /// <summary>
    /// 音频编码 id.
    /// </summary>
    public string? AudioCodecId { get; init; }

    /// <summary>
    /// 音频标称带宽.
    /// </summary>
    public long AudioBandwidth { get; init; }

    /// <summary>
    /// 日志用显示名（不要放 URL）.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// 包装结果：<see cref="Proxied"/> 为 false 时两个地址就是传进来的原地址.
/// </summary>
/// <param name="VideoUrl">视频地址（代理地址或原地址）.</param>
/// <param name="AudioUrl">音频地址（代理地址或原地址）.</param>
/// <param name="Proxied">是否走了代理.</param>
internal readonly record struct MediaProxyWrappedSource(string VideoUrl, string? AudioUrl, bool Proxied);

/// <summary>
/// 媒体字节范围代理的应用侧门面：读设置、起停代理、把 ILogger 接进来、给解析器一步完成的包装.
/// <para>
/// 用途（D / E 接力时按这个用）：
/// <list type="number">
/// <item>解析器拿到 videoUrl / audioUrl 之后，调 <see cref="TryWrap"/>，把返回的两个地址交给 mpv；</item>
/// <item>换集 / 换视频时会自动释放上一场播放的登记（按 <see cref="MediaProxySourceRequest.OwnerKey"/>）；</item>
/// <item>设置里 <c>PrefetchProxyEnabled</c> 关着、或代理起不来时，<see cref="TryWrap"/> 原样返回，调用方无需分支.</item>
/// </list>
/// </para>
/// </summary>
internal static class MediaBufferProxyBridge
{
    /// <summary>
    /// 环境变量开关（没 UI 时用来试跑；值为 1/true/yes/on 视为开启）.
    /// </summary>
    public const string EnvironmentSwitch = "BILICOPILOT_MEDIA_PROXY";

    private static readonly object Gate = new();
    private static MediaProxy? _state;

    /// <summary>
    /// 是否启用媒体代理：设置项 <c>PrefetchProxyEnabled</c>（默认 false）或环境变量.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            var flag = Environment.GetEnvironmentVariable(EnvironmentSwitch);
            if (!string.IsNullOrEmpty(flag)
                && (flag.Equals("1", StringComparison.Ordinal)
                    || flag.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || flag.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || flag.Equals("on", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            try
            {
                return SettingsToolkit.ReadLocalSetting(SettingNames.PrefetchProxyEnabled, false);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 已启动的代理实例；未启用或启动失败时为 null.
    /// </summary>
    public static MediaBufferProxy? Shared
    {
        get
        {
            lock (Gate)
            {
                return _state?.Proxy;
            }
        }
    }

    /// <summary>
    /// 从 dash 信息里挑出与当前画质对应的段，拼一条代理请求.
    /// 解析器只需要调这一个方法，不用自己去找 segment.
    /// </summary>
    /// <param name="ownerKey">归属键（换集 / 换视频时按它批量释放）.</param>
    /// <param name="bvid">视频 BV 号（番剧用 epId / seasonId）.</param>
    /// <param name="cid">分 P cid.</param>
    /// <param name="displayName">日志显示名.</param>
    /// <param name="videoUrl">视频原地址.</param>
    /// <param name="audioUrl">音频原地址，可为 null.</param>
    /// <param name="playbackInfo">dash 信息，可为 null（拿不到时用保守默认值）.</param>
    /// <param name="quality">当前画质 qn.</param>
    /// <param name="preferCodec">偏好编码 id（AppToolkit.GetPreferCodecId）.</param>
    /// <returns>代理请求.</returns>
    public static MediaProxySourceRequest CreateRequest(
        string ownerKey,
        string bvid,
        string cid,
        string displayName,
        string videoUrl,
        string? audioUrl,
        DashMediaInformation? playbackInfo,
        int quality,
        string preferCodec)
    {
        var qualityText = quality.ToString(CultureInfo.InvariantCulture);
        var videos = playbackInfo?.Videos?.ToList() ?? [];
        var audios = playbackInfo?.Audios?.ToList() ?? [];
        var videoSegment = videos.FirstOrDefault(p => p.Id == qualityText && (p.Codecs?.Contains(preferCodec, StringComparison.Ordinal) ?? false))
            ?? videos.FirstOrDefault(p => p.Id == qualityText)
            ?? videos.FirstOrDefault();
        var audioSegment = audios.Count == 0
            ? null
            : audios.OrderByDescending(p => ParseSegmentId(p.Id)).First();

        return new MediaProxySourceRequest
        {
            OwnerKey = ownerKey,
            Bvid = bvid,
            Cid = cid,
            QualityId = qualityText,
            CodecId = videoSegment?.CodecId?.ToString(CultureInfo.InvariantCulture),
            MimeType = string.IsNullOrEmpty(videoSegment?.MimeType) ? "video/mp4" : videoSegment.MimeType,
            Bandwidth = videoSegment?.Bandwidth ?? 0,
            Initialization = videoSegment?.Initialization,
            IndexRange = videoSegment?.IndexRange,
            VideoUrl = videoUrl,
            AudioUrl = audioUrl,
            AudioQualityId = audioSegment?.Id,
            AudioMimeType = string.IsNullOrEmpty(audioSegment?.MimeType) ? "audio/mp4" : audioSegment.MimeType,
            AudioCodecId = audioSegment?.CodecId?.ToString(CultureInfo.InvariantCulture),
            AudioBandwidth = audioSegment?.Bandwidth ?? 0,
            DisplayName = displayName,
        };
    }

    private static int ParseSegmentId(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    /// <summary>
    /// 判断地址是不是本代理给的（避免重复包装）.
    /// </summary>
    /// <param name="url">播放地址.</param>
    /// <returns>是否代理地址.</returns>
    public static bool IsProxyUrl(string? url) => MediaBufferProxy.IsProxyUrl(url);

    /// <summary>
    /// 确保代理已启动（幂等）.
    /// </summary>
    /// <param name="logger">日志.</param>
    /// <returns>代理实例；启动失败返回 null.</returns>
    public static MediaBufferProxy? EnsureStarted(ILogger? logger)
    {
        lock (Gate)
        {
            if (_state?.Proxy is { IsRunning: true } running)
            {
                return running;
            }

            try
            {
                var proxy = _state?.Proxy ?? new MediaBufferProxy();
                proxy.Log = (level, message) => LogFromProxy(logger, level, message);
                proxy.Start();
                _state ??= new MediaProxy();
                _state.Proxy = proxy;
                _state.StartFailureLogged = false;
                logger?.LogInformation("媒体字节范围代理已启动，端口 {Port}", proxy.Port);
                return proxy;
            }
            catch (Exception ex)
            {
                if (_state is null || !_state.StartFailureLogged)
                {
                    logger?.LogWarning(ex, "媒体字节范围代理启动失败（端口/权限），回退原播放地址");
                    if (_state is not null)
                    {
                        _state.StartFailureLogged = true;
                    }
                }

                return null;
            }
        }
    }

    /// <summary>
    /// 把一对音视频地址包成代理地址；任何一步失败都原样返回（保证播放不受影响）.
    /// </summary>
    /// <param name="request">媒体请求.</param>
    /// <param name="cookie">B 站 Cookie（上游鉴权要用，只在本进程内传递）.</param>
    /// <param name="logger">日志.</param>
    /// <returns>包装结果.</returns>
    public static MediaProxyWrappedSource TryWrap(MediaProxySourceRequest request, string? cookie, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(request);
        var original = new MediaProxyWrappedSource(request.VideoUrl, request.AudioUrl, false);
        if (!IsEnabled || string.IsNullOrEmpty(request.VideoUrl) || MediaBufferProxy.IsProxyUrl(request.VideoUrl))
        {
            return original;
        }

        var proxy = EnsureStarted(logger);
        if (proxy is null)
        {
            return original;
        }

        try
        {
            var videoIdentity = MediaStreamIdentity.Create(
                request.Bvid,
                request.Cid,
                request.QualityId,
                request.CodecId,
                request.MimeType,
                request.Bandwidth,
                request.Initialization,
                request.IndexRange,
                request.VideoUrl);

            var videoRegistration = proxy.Register(new MediaStreamSourceDescriptor(request.VideoUrl, videoIdentity)
            {
                Cookie = cookie,
                Referer = InternalHttpExtensions.VideoReferer,
                UserAgent = InternalHttpExtensions.VideoUserAgent,
                DisplayName = request.DisplayName,
                OwnerKey = request.OwnerKey,
            });

            string? audioProxyUrl = null;
            MediaProxyRegistration? audioRegistration = null;
            if (!string.IsNullOrEmpty(request.AudioUrl) && !MediaBufferProxy.IsProxyUrl(request.AudioUrl))
            {
                try
                {
                    var audioIdentity = MediaStreamIdentity.Create(
                        request.Bvid,
                        request.Cid,
                        request.AudioQualityId ?? (request.QualityId + "-audio"),
                        request.AudioCodecId,
                        request.AudioMimeType ?? "audio/mp4",
                        request.AudioBandwidth,
                        null,
                        null,
                        request.AudioUrl);
                    audioRegistration = proxy.Register(new MediaStreamSourceDescriptor(request.AudioUrl, audioIdentity)
                    {
                        Cookie = cookie,
                        Referer = InternalHttpExtensions.VideoReferer,
                        UserAgent = InternalHttpExtensions.VideoUserAgent,
                        DisplayName = request.DisplayName + " · 音频",
                        OwnerKey = request.OwnerKey,
                    });
                    audioProxyUrl = audioRegistration.ProxyUrl;
                }
                catch (Exception ex)
                {
                    // 音频没接管成功不算致命：视频照用代理，音频退回原地址.
                    logger?.LogWarning(ex, "音频轨接管代理失败，音频回退原地址");
                }
            }

            var previousOwner = _state?.ActiveOwnerKey;
            if (!string.IsNullOrEmpty(previousOwner) && !string.Equals(previousOwner, request.OwnerKey, StringComparison.Ordinal))
            {
                // 换集 / 换源：上一路的登记和它的缓存租约在这里释放，别让旧条目被租约钉住.
                proxy.ReleaseAllOwnedBy(previousOwner);
                logger?.LogInformation("媒体代理释放上一路登记：{Owner}", previousOwner);
            }
            else
            {
                // 同一归属键重复包装（换画质 / 换播放地址）：旧的那几条已经没有播放器在用，
                // 只留这次刚登记的两条，避免同一集攒下多份租约.
                var keep = audioRegistration is null
                    ? new[] { videoRegistration.Id }
                    : new[] { videoRegistration.Id, audioRegistration.Id };
                proxy.ReleaseAllOwnedBy(request.OwnerKey, keep);
            }

            if (_state is not null)
            {
                _state.ActiveOwnerKey = request.OwnerKey;
            }

            logger?.LogInformation(
                "媒体代理已接管播放地址：{Name}（qn={Quality}，端口 {Port}）",
                request.DisplayName,
                request.QualityId,
                proxy.Port);

            return new MediaProxyWrappedSource(videoRegistration.ProxyUrl, audioProxyUrl ?? request.AudioUrl, true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "媒体代理登记失败，回退原播放地址");
            return original;
        }
    }

    /// <summary>
    /// 换集 / 换源时调用：如果上一路播放的归属键和 <paramref name="nextOwnerKey"/> 不同，
    /// 就释放上一路的全部登记（连带缓存租约）。
    /// <para>
    /// 解析器是 transient 的（每次初始化都是新实例），所以「上一路的归属键」由本桥接层持有，
    /// 解析器只负责在 Initialize 里报一次「这一路是谁」。
    /// </para>
    /// </summary>
    /// <param name="nextOwnerKey">这一路的归属键，形如 <c>player:{bvid}:{cid}</c>.</param>
    public static void ReleasePreviousOwner(string nextOwnerKey)
    {
        string? previous;
        lock (Gate)
        {
            previous = _state?.ActiveOwnerKey;
        }

        if (string.IsNullOrEmpty(previous) || string.Equals(previous, nextOwnerKey, StringComparison.Ordinal))
        {
            return;
        }

        ReleaseOwner(previous);
    }

    /// <summary>
    /// 释放一个归属键下的全部登记（换集 / 换源 / 播放器销毁时用）.
    /// </summary>
    /// <param name="ownerKey">归属键.</param>
    public static void ReleaseOwner(string ownerKey)
    {
        var proxy = Shared;
        if (proxy is null || string.IsNullOrEmpty(ownerKey))
        {
            return;
        }

        proxy.ReleaseAllOwnedBy(ownerKey);
        lock (Gate)
        {
            if (_state is not null && string.Equals(_state.ActiveOwnerKey, ownerKey, StringComparison.Ordinal))
            {
                _state.ActiveOwnerKey = null;
            }
        }
    }

    /// <summary>
    /// 释放归属键以某个前缀开头的全部登记（拿不到 cid 时按 bvid 前缀整片释放）.
    /// </summary>
    /// <param name="prefix">归属键前缀，例如 <c>player:BV1xx:</c>.</param>
    public static void ReleaseOwnerPrefix(string prefix)
    {
        var proxy = Shared;
        if (proxy is null || string.IsNullOrEmpty(prefix))
        {
            return;
        }

        proxy.ReleaseAllOwnedByPrefix(prefix);
        lock (Gate)
        {
            if (_state?.ActiveOwnerKey is { } active && active.StartsWith(prefix, StringComparison.Ordinal))
            {
                _state.ActiveOwnerKey = null;
            }
        }
    }

    /// <summary>
    /// 取统计（诊断面板 / 日志用）.
    /// </summary>
    /// <returns>统计；代理没起来时返回 null.</returns>
    public static MediaProxyStatistics? GetStatistics() => Shared?.GetStatistics();

    /// <summary>
    /// 停止代理并释放监听（应用退出时调；重复调用安全）.
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            var proxy = _state?.Proxy;
            _state = null;
            proxy?.Dispose();
        }
    }

    private static void LogFromProxy(ILogger? logger, MediaProxyLogLevel level, string message)
    {
        if (logger is null)
        {
            return;
        }

        var text = "媒体代理：" + message;
        switch (level)
        {
            case MediaProxyLogLevel.Warning:
                logger.LogWarning("{Message}", text);
                break;
            case MediaProxyLogLevel.Error:
                logger.LogError("{Message}", text);
                break;
            case MediaProxyLogLevel.Information:
                logger.LogInformation("{Message}", text);
                break;
            default:
                logger.LogDebug("{Message}", text);
                break;
        }
    }

    /// <summary>
    /// 桥接层的可变状态（放在一个对象里，锁住 <see cref="Gate"/> 访问）.
    /// </summary>
    private sealed class MediaProxy
    {
        public MediaBufferProxy? Proxy { get; set; }

        public string? ActiveOwnerKey { get; set; }

        public bool StartFailureLogged { get; set; }
    }
}
