// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Services;

/// <summary>
/// CDN 加速器.
/// 逻辑参考 Bilibili Accelerator Lite 用户脚本：识别 PCDN/MCDN 链接，按模式替换主机，
/// 用候选池实测速度排名优选，卡顿或失败时轮换到下一个节点.
/// </summary>
internal static class CdnAccelerator
{
    /// <summary>
    /// 默认的媒体代理主机（脚本里的 proxyHost），把 MCDN 地址整体包一层代理.
    /// </summary>
    public const string MediaProxyHost = "proxy-tf-all-ws.bilivideo.com";

    /// <summary>
    /// 候选 CDN 主机池（取自用户脚本）.
    /// </summary>

    /// <summary>
    /// 内置的公共解析服务器（可自行替换）.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownProxyServers = new[]
    {
        "https://bili.nepnep.moe",
        "https://b.chuchai.vip",
        "https://api.example.com",
    };

    /// <summary>
    /// 当前使用的节点发生变化：自动优选换了节点、或用户手动切换.
    /// </summary>
    public static event EventHandler? HostChanged;

    private static readonly ConcurrentDictionary<string, HostSpeed> Speeds = new();
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static DateTime _lastProbeTime = DateTime.MinValue;
    private static int _rotateCursor;

    /// <summary>
    /// 是否启用 CDN 替换.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.ReplaceCdnEnabled, true);

    /// <summary>
    /// 给界面显示的当前节点（去掉了 .bilivideo.com 后缀）.
    /// </summary>
    public static string ActiveHostDisplay
    {
        get
        {
            if (!IsEnabled || Mode == CdnReplaceMode.None)
            {
                return string.Empty;
            }

            var host = CurrentHost;
            const string suffix = ".bilivideo.com";
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? host[..^suffix.Length]
                : host;
        }
    }

    /// <summary>
    /// 开关 CDN 替换.
    /// </summary>
    /// <param name="value">是否启用.</param>
    public static void SetEnabled(bool value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.ReplaceCdnEnabled, value);
        CdnStatusBus.Report(value
            ? $"CDN 优选已开启，当前节点 {(string.IsNullOrWhiteSpace(ActiveHostDisplay) ? "默认" : ActiveHostDisplay)}"
            : "CDN 优选已关闭，走官方默认线路");
        RaiseHostChanged();
    }

    /// <summary>
    /// 通知界面刷新节点显示.
    /// </summary>
    public static void RaiseHostChanged()
        => HostChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>
    /// 当前替换模式.
    /// </summary>
    public static CdnReplaceMode Mode
        => SettingsToolkit.ReadLocalSetting(SettingNames.CdnReplaceMode, CdnReplaceMode.PcdnOnly);

    /// <summary>
    /// 是否自动优选节点.
    /// </summary>
    public static bool AutoSelect
        => SettingsToolkit.ReadLocalSetting(SettingNames.CdnAutoSelect, true);

    /// <summary>
    /// 卡顿时自动切线.
    /// </summary>
    public static bool StallRecovery
        => SettingsToolkit.ReadLocalSetting(SettingNames.CdnStallSwitch, true);

    /// <summary>
    /// 当前使用的主机.
    /// </summary>
    public static string CurrentHost
    {
        get
        {
            var ranked = GetRankedHosts();
            if (ranked.Count > 0 && AutoSelect)
            {
                return ranked[0];
            }

            var configured = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerCdnServer, PlayUrlOptimizer.DefaultCdnHost);
            return string.IsNullOrWhiteSpace(configured) ? PlayUrlOptimizer.DefaultCdnHost : configured;
        }
    }

    /// <summary>
    /// 判断链接类型.
    /// </summary>
    public static (bool IsMcdn, bool IsPcdn, bool IsSlow) Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return (false, false, false);
        }

        var host = uri.Host;
        var isMcdn = System.Text.RegularExpressions.Regex.IsMatch(host, @"\.mcdn\.bilivideo\.(?:cn|com|net)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var portHeuristic = SettingsToolkit.ReadLocalSetting(SettingNames.CdnFilterPort, true);
        var isIp = IPAddress.TryParse(host, out _);
        var isPcdn =
            isIp
            || isMcdn
            || (portHeuristic && !string.IsNullOrEmpty(uri.Port.ToString(CultureInfo.InvariantCulture)) && uri.Port is not 80 and not 443 and not -1)
            || uri.Query.Contains("os=mcdn", StringComparison.OrdinalIgnoreCase)
            || host.Equals("upos-sz-mirror14b.bilivideo.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".szbdyd.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".mountaintoys.cn", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".nexusedgeio.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".ahdohpiechei.com", StringComparison.OrdinalIgnoreCase);

        var rewriteAkamai = SettingsToolkit.ReadLocalSetting(SettingNames.CdnRewriteAkamai, false);
        var isSlow = isPcdn
            || (rewriteAkamai && host.EndsWith(".akamaized.net", StringComparison.OrdinalIgnoreCase));

        return (isMcdn, isPcdn, isSlow);
    }

    /// <summary>
    /// 按当前设置改写播放链接.
    /// </summary>
    /// <param name="url">原始播放地址.</param>
    /// <param name="fromProxy">该地址是否来自代理服务器.</param>
    public static string Rewrite(string url, bool fromProxy = false)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var mode = Mode;
        if (mode == CdnReplaceMode.None)
        {
            return url;
        }

        if (mode == CdnReplaceMode.ProxyOnly && !fromProxy)
        {
            return url;
        }

        var (isMcdn, _, isSlow) = Classify(url);
        var targetHost = CurrentHost;

        // 只替换 PCDN 时，非慢速链接保持原样.
        if (!isSlow && !isMcdn && mode != CdnReplaceMode.All)
        {
            return url;
        }

        var replaced = ReplaceHost(url, targetHost);
        return replaced ?? url;
    }

    /// <summary>
    /// 把链接里的 CDN 主机换成指定主机（不管当前开关）.
    /// </summary>
    /// <param name="url">原始地址.</param>
    /// <param name="host">目标主机.</param>
    /// <returns>替换后的地址；失败返回空串.</returns>
    public static string SwapHost(string url, string host)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var replaced = ReplaceHost(url, host);
        if (string.IsNullOrEmpty(replaced) || replaced == url)
        {
            return string.Empty;
        }

        var display = Uri.TryCreate(replaced, UriKind.Absolute, out var uri) ? uri.Host : host;
        if (!string.Equals(display, host, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return replaced;
    }

    /// <summary>
    /// 卡顿或失败时轮换节点.
    /// </summary>
    public static string RotateHost()
    {
        var pool = GetRankedHosts();
        if (pool.Count == 0)
        {
            pool = CdnPoolService.GetCandidates();
        }

        if (pool.Count > 1)
        {
            _rotateCursor = (_rotateCursor + 1) % pool.Count;
        }

        RaiseHostChanged();
        CdnStatusBus.Report($"卡顿换线：{pool[_rotateCursor]}");
        return pool[_rotateCursor];
    }

    /// <summary>
    /// 用样本地址实测候选主机速度并排名.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RankHostsAsync(string sampleUrl, ILogger? logger = null)
    {
        if (!AutoSelect || string.IsNullOrWhiteSpace(sampleUrl))
        {
            return GetRankedHosts();
        }

        if (DateTime.UtcNow - _lastProbeTime < TimeSpan.FromHours(6) && Speeds.Count > 0)
        {
            return GetRankedHosts();
        }

        var tasks = CdnPoolService.GetCandidates().Select(host => ProbeHostAsync(host, sampleUrl)).ToList();
        var results = await Task.WhenAll(tasks);
        var valid = results
            .Where(p => p.Mbps > 0)
            .OrderByDescending(p => p.Mbps)
            .ThenBy(p => p.Ttfb)
            .ToList();

        if (valid.Count == 0)
        {
            return GetRankedHosts();
        }

        Speeds.Clear();
        for (var i = 0; i < valid.Count; i++)
        {
            Speeds[valid[i].Host] = valid[i];
        }

        _lastProbeTime = DateTime.UtcNow;
        var ranking = valid.Select(p => p.Host).ToList();
        SettingsToolkit.WriteLocalSetting(SettingNames.CdnRankingCache, string.Join(",", ranking));
        logger?.LogInformation("CDN 优选完成：{Ranking}", string.Join(" > ", ranking));
        RaiseHostChanged();
        return ranking;
    }

    /// <summary>
    /// 读已缓存的排名.
    /// </summary>
    public static IReadOnlyList<string> GetRankedHosts()
    {
        if (Speeds.Count > 0)
        {
            return Speeds.OrderByDescending(p => p.Value.Mbps).Select(p => p.Key).ToList();
        }

        var cached = SettingsToolkit.ReadLocalSetting(SettingNames.CdnRankingCache, string.Empty);
        if (string.IsNullOrWhiteSpace(cached))
        {
            return Array.Empty<string>();
        }

        return cached.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// 测试指定主机的延迟（毫秒），失败返回 -1.
    /// </summary>
    public static async Task<int> TestLatencyAsync(string host, string sampleUrl, string userAgent, string referer)
    {
        var target = string.IsNullOrWhiteSpace(sampleUrl) ? $"https://{host}/" : ReplaceHost(sampleUrl, host);
        if (target is null)
        {
            return -1;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Referer", referer);
            request.Headers.Range = new RangeHeaderValue(0, 1023);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            stopwatch.Stop();
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent)
            {
                return (int)stopwatch.ElapsedMilliseconds;
            }

            // 节点对样本链接回 403/404（路径通常只对带签名的地址有效），退回 TCP 握手测速.
            return await ProbeTcpLatencyAsync(host).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 样本链接不可用时退化成 TCP 握手测速：节点活着就能给个大致延迟.
            return await ProbeTcpLatencyAsync(host).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// 直接连 443，粗略衡量节点可达性与延迟.
    /// </summary>
    /// <param name="host">CDN 主机.</param>
    /// <returns>毫秒；失败 -1.</returns>
    private static async Task<int> ProbeTcpLatencyAsync(string host)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(host, 443, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return client.Connected ? (int)stopwatch.ElapsedMilliseconds : -1;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "CDN 节点 TCP 测速失败：{Host}", host);
            return -1;
        }
    }

    private static async Task<HostSpeed> ProbeHostAsync(string host, string sampleUrl)
    {
        var target = ReplaceHost(sampleUrl, host);
        if (target is null)
        {
            return new HostSpeed(host, 0, int.MaxValue);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
            {
                return new HostSpeed(host, 0, int.MaxValue);
            }

            var ttfb = (int)stopwatch.ElapsedMilliseconds;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[64 * 1024];
            var total = 0;
            var readStart = Stopwatch.StartNew();
            while (total < 768 * 1024)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            readStart.Stop();
            var mbps = readStart.ElapsedSeconds() <= 0
                ? 0
                : (total * 8 / 1_000_000d) / readStart.ElapsedSeconds();
            return new HostSpeed(host, mbps, ttfb);
        }
        catch (Exception)
        {
            return new HostSpeed(host, 0, int.MaxValue);
        }
    }

    private static double ElapsedSeconds(this Stopwatch stopwatch) => stopwatch.Elapsed.TotalSeconds;

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
            Scheme = Uri.UriSchemeHttps,
        };

        return builder.Uri.AbsoluteUri;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private readonly record struct HostSpeed(string Host, double Mbps, int Ttfb);
}
