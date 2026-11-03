// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using BiliCopilot.UI.Models.Constants;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Toolkits;

/// <summary>
/// 播放地址优化器.
/// 参考 BiliLite 的做法：优先选择非 PCDN 的镜像地址，其次做可达性校验，最后用固定 CDN 镜像替换主机名兜底.
/// </summary>
internal static class PlayUrlOptimizer
{
    /// <summary>
    /// 默认 CDN 镜像.
    /// </summary>
    public const string DefaultCdnHost = "upos-sz-mirrorhwo1.bilivideo.com";

    private static readonly string[] FallbackCdnHosts =
    [
        DefaultCdnHost,
        "upos-sz-mirrorcos.bilivideo.com",
        "upos-sz-mirrorali.bilivideo.com",
        "upos-sz-mirrorhw.bilivideo.com",
    ];

    private static readonly ConcurrentDictionary<string, bool> HostProbeCache = new();
    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>
    /// 在若干候选地址中挑一个可用的播放地址.
    /// </summary>
    public static async Task<string> PickPlayableUrlAsync(
        string? baseUrl,
        IEnumerable<string>? backupUrls,
        string userAgent,
        string referer,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl ?? string.Empty;
        }

        var allUrls = new List<string> { baseUrl };
        if (backupUrls is not null)
        {
            allUrls.AddRange(backupUrls.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        var mirrorUrls = allUrls.Where(p => !AppToolkit.IsP2PUrl(p)).Distinct().ToList();
        var pcdnUrls = allUrls.Where(AppToolkit.IsP2PUrl).Distinct().ToList();

        // 1. 先用非 PCDN 的原始地址，逐个验活.
        foreach (var url in mirrorUrls)
        {
            if (await IsReachableAsync(url, userAgent, referer))
            {
                logger?.LogInformation("播放地址可用，直接使用：{Host}", GetHost(url));
                return url;
            }
        }

        // 2. 主机名替换成固定 CDN 镜像后验活.
        foreach (var host in GetCdnHosts())
        {
            foreach (var url in allUrls)
            {
                var replaced = ReplaceHost(url, host);
                if (replaced is null)
                {
                    continue;
                }

                if (await IsReachableAsync(replaced, userAgent, referer))
                {
                    logger?.LogInformation("播放地址已替换 CDN 主机：{From} => {To}", GetHost(url), host);
                    return replaced;
                }
            }
        }

        // 3. 兜底：优先非 PCDN 地址，其次原始地址.
        var fallback = mirrorUrls.FirstOrDefault() ?? pcdnUrls.FirstOrDefault() ?? baseUrl;
        logger?.LogWarning("未能验证播放地址可用性，回退到 {Host}", GetHost(fallback));
        return fallback;
    }

    private static List<string> GetCdnHosts()
    {
        var customHost = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerCdnServer, DefaultCdnHost);
        var hosts = new List<string>();
        if (!string.IsNullOrWhiteSpace(customHost))
        {
            hosts.Add(customHost);
        }

        hosts.AddRange(FallbackCdnHosts);
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static string? ReplaceHost(string url, string host)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Host = host,
            Port = -1,
        };

        return builder.Uri.AbsoluteUri;
    }

    private static async Task<bool> IsReachableAsync(string url, string userAgent, string referer)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (HostProbeCache.TryGetValue(uri.Host, out var cached))
        {
            return cached;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.Range = new RangeHeaderValue(0, 9);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var success = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent;
            if (success)
            {
                HostProbeCache[uri.Host] = true;
            }

            return success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
    }
}