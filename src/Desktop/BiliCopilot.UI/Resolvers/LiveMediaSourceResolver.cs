// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.MpvKernel.Core.Models;
using Richasy.MpvKernel.Player;
using Richasy.MpvKernel.Player.Models;

namespace BiliCopilot.UI.Resolvers;

/// <summary>
/// 直播媒体源解析器.
/// 把直播间解析为 MPV 可直接播放的 HLS 地址，让直播复用视频播放器.
/// </summary>
internal sealed partial class LiveMediaSourceResolver(IPlayerService playerService, IBiliCookiesResolver cookiesResolver, ILogger<LiveMediaSourceResolver> logger) : MediaSourceResolverBase
{
    private LiveInformation? _live;
    private string? _cachedUrl;

    /// <summary>
    /// 初始化直播信息.
    /// </summary>
    public void Initialize(LiveInformation live)
    {
        _live = live;
        _cachedUrl = null;
    }

    /// <inheritdoc/>
        public void UseUrl(string url) => _cachedUrl = url;

    /// <inheritdoc/>
    public override IMpvMediaSourceResolver Clone() => throw new NotImplementedException();

    /// <inheritdoc/>
    public override async Task<MpvMediaSource> GetSourceAsync()
    {
        if (_live is null)
        {
            throw new InvalidOperationException("直播信息未初始化.");
        }

        var volume = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerVolume, 100d);
        var cookies = cookiesResolver.GetCookieString();
        var headers = new Dictionary<string, string>
        {
            { "Cookie", cookies },
            { "Referer", InternalHttpExtensions.LiveReferer },
        };
        var options = new MpvPlayOptions
        {
            WindowHandle = WindowHandle,
            InitialVolume = volume,
            InitialSpeed = 1d,
            UserAgent = InternalHttpExtensions.LiveUserAgent,
            HttpHeaders = headers,
            EnableCookies = true,
            MediaName = _live.Identifier.Title,
        };

        var url = await GetPlayUrlAsync();
        return new MpvMediaSource(url, _live.Identifier.Id, _live.Identifier.Title, options);
    }

    private async Task<string> GetPlayUrlAsync()
    {
        if (!string.IsNullOrEmpty(_cachedUrl))
        {
            return _cachedUrl!;
        }

        var detail = await playerService.GetLivePlayDetailAsync(_live!.Identifier, 10000, true, default);
        var urls = detail?.Lines?
            .SelectMany(line => line.Urls)
            .Select(item => item?.ToString())
            .Where(item => !string.IsNullOrEmpty(item))
            .Select(item => item!)
            .ToList() ?? new List<string>();

        var url = urls.FirstOrDefault(item => item.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            ?? urls.FirstOrDefault();

        if (string.IsNullOrEmpty(url))
        {
            logger.LogWarning("未能获取直播间 {Id} 的播放地址.", _live.Identifier.Id);
            throw new InvalidOperationException("未能获取直播播放地址.");
        }

        // 直播地址同样可能落在 PCDN 主机上，走一遍地址可用性校验与 CDN 替换.
        var optimizedUrl = await PlayUrlOptimizer.PickPlayableUrlAsync(
            url,
            urls.Where(p => p != url).ToList(),
            InternalHttpExtensions.LiveUserAgent,
            InternalHttpExtensions.LiveReferer,
            logger,
            accelerate: false);

        _cachedUrl = optimizedUrl;
        return optimizedUrl;
    }
}
