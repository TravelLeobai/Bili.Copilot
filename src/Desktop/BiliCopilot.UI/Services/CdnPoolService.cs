// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// CDN 节点池.
/// 参考 CCB（Custom CDN of Bilibili）：它的服务端每天用被动 DNS 接口枚举
/// <c>*.bilivideo.com</c> 子域并按地区归类，客户端再从 region 里挑节点.
/// 这里直接复用它公开的 <c>data/cdn.json</c>，另加两个子域接口兜底，
/// 拿到的池子交给 <see cref="CdnAccelerator"/> 实测优选.
/// </summary>
internal static class CdnPoolService
{
    /// <summary>
    /// CCB 维护的节点池（按地区分组）.
    /// </summary>
    public const string CcbPoolUrl = "https://raw.githubusercontent.com/Kanda-Akihito-Kun/ccb/main/data/cdn.json";

    /// <summary>
    /// 被动 DNS 子域枚举接口.
    /// </summary>
    public const string SrcLabUrl = "https://srclab.cn/api/server/dnsDetect/?domain=bilivideo.com";

    /// <summary>
    /// 内置兜底节点（原先写死的 upos 家族）.
    /// </summary>
    public static readonly IReadOnlyList<string> SeedHosts = new[]
    {
        "upos-sz-mirrorcosov.bilivideo.com",
        "upos-sz-mirroraliov.bilivideo.com",
        "upos-sz-mirrorhwov.bilivideo.com",
        "upos-sz-mirrorali.bilivideo.com",
        "upos-tf-all-hw.bilivideo.com",
        "upos-sz-mirrorhw.bilivideo.com",
        "upos-sz-mirrorcos.bilivideo.com",
        "upos-tf-all-tx.bilivideo.com",
    };

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = IpV4Connector.ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// 池子里有多少个节点.
    /// </summary>
    public static int Count
        => ReadPool().Count;

    /// <summary>
    /// 上次同步时间.
    /// </summary>
    public static DateTimeOffset LastSync
        => DateTimeOffset.FromUnixTimeSeconds(SettingsToolkit.ReadLocalSetting(SettingNames.CdnPoolLastSync, 0L)).ToLocalTime();

    /// <summary>
    /// 是否需要同步（超过 24 小时）.
    /// </summary>
    public static bool NeedSync => DateTimeOffset.Now - LastSync > TimeSpan.FromHours(24);

    /// <summary>
    /// 状态文本.
    /// </summary>
    public static string StatusText
        => Count == 0
            ? "节点池为空，正在使用内置节点"
            : $"{Count} 个节点 · 上次同步 {LastSync:MM-dd HH:mm}";

    /// <summary>
    /// 取用于测速的候选节点：内置种子 + 从池子里按地区轮采样的节点.
    /// </summary>
    /// <param name="max">最多返回多少个.</param>
    public static IReadOnlyList<string> GetCandidates(int max = 24)
    {
        var result = new List<string>(SeedHosts);
        var pool = ReadPool();
        foreach (var host in pool)
        {
            if (result.Count >= max)
            {
                break;
            }

            if (!result.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(host);
            }
        }

        return result;
    }

    /// <summary>
    /// 同步节点池.
    /// 依次尝试 CCB 的 cdn.json 与被动 DNS 接口，成功才覆盖本地缓存.
    /// </summary>
    /// <param name="logger">日志.</param>
    public static async Task<CdnPoolSyncResult> SyncAsync(ILogger? logger = null)
    {
        var result = new CdnPoolSyncResult();

        var hosts = await FetchFromCcbAsync(logger).ConfigureAwait(false);
        if (hosts.Count > 0)
        {
            result.Source = "CCB cdn.json";
        }
        else
        {
            hosts = await FetchFromDnsDetectAsync(logger).ConfigureAwait(false);
            if (hosts.Count > 0)
            {
                result.Source = "被动 DNS 接口";
            }
        }

        if (hosts.Count == 0)
        {
            logger?.LogWarning("CDN 节点池同步失败，继续使用 {Count} 个已缓存节点", Count);
            result.Count = Count;
            return result;
        }

        // 采样：跨地区交替取，保证不同机房都有代表，再交给测速排名.
        var sampled = SampleByRegion(hosts, 48);
        SettingsToolkit.WriteLocalSetting(SettingNames.CdnPoolCache, string.Join(",", sampled));
        SettingsToolkit.WriteLocalSetting(SettingNames.CdnPoolLastSync, DateTimeOffset.Now.ToUnixTimeSeconds());
        result.Count = sampled.Count;
        logger?.LogInformation("CDN 节点池已更新：{Count} 个（来源：{Source}）", sampled.Count, result.Source);
        return result;
    }

    private static async Task<List<string>> FetchFromCcbAsync(ILogger? logger)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CcbPoolUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var region in document.RootElement.EnumerateObject())
            {
                if (region.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in region.Value.EnumerateArray())
                {
                    var host = Normalize(item.GetString());
                    if (host is not null)
                    {
                        result.Add(host);
                    }
                }
            }

            return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "读取 CCB 节点池失败");
            return [];
        }
    }

    private static async Task<List<string>> FetchFromDnsDetectAsync(ILogger? logger)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SrcLabUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/134.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("subdomains", out var subdomains)
                || subdomains.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var item in subdomains.EnumerateArray())
            {
                var host = Normalize(item.GetString());
                if (host is not null)
                {
                    result.Add(host);
                }
            }

            return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "被动 DNS 接口查询失败");
            return [];
        }
    }

    /// <summary>
    /// 跨地区交替采样，避免只取到某个机房.
    /// </summary>
    private static List<string> SampleByRegion(IReadOnlyList<string> hosts, int max)
    {
        var groups = hosts
            .GroupBy(p => GetRegionPrefix(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new Queue<string>(g.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)));

        var result = new List<string>();
        while (result.Count < max && groups.Count > 0)
        {
            foreach (var key in groups.Keys.ToList())
            {
                var queue = groups[key];
                if (queue.Count == 0)
                {
                    groups.Remove(key);
                    continue;
                }

                result.Add(queue.Dequeue());
                if (result.Count >= max)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static string GetRegionPrefix(string host)
    {
        // cn-sh-ct-01-01.bilivideo.com → cn-sh
        var parts = host.Split('.')[0].Split('-');
        return parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : host;
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var host = raw.Trim().ToLowerInvariant();
        if (!host.Contains('.'))
        {
            host += ".bilivideo.com";
        }

        if (!host.EndsWith(".bilivideo.com", StringComparison.Ordinal))
        {
            return null;
        }

        // 和 CCB 一样排除 origin / all 这类不适合直连的节点.
        if (host.Contains("origin") || host.Contains("all"))
        {
            return null;
        }

        return host;
    }

    private static List<string> ReadPool()
    {
        var cache = SettingsToolkit.ReadLocalSetting(SettingNames.CdnPoolCache, string.Empty);
        return string.IsNullOrWhiteSpace(cache)
            ? []
            : [.. cache.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}

/// <summary>
/// 节点池同步结果.
/// </summary>
internal sealed class CdnPoolSyncResult
{
    public int Count { get; set; }

    public string? Source { get; set; }

    public bool Success => Count > 0;
}