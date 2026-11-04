// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 需要跳过的片段.
/// </summary>
public sealed class SponsorSegment
{
    /// <summary>
    /// 开始时间（秒）.
    /// </summary>
    public double Start { get; set; }

    /// <summary>
    /// 结束时间（秒）.
    /// </summary>
    public double End { get; set; }

    /// <summary>
    /// 片段分类（sponsor / intro / ai / cloud 等）.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// 片段来源（sponsorblock / cloud / ai）.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 动作类型（skip / mute / full），对齐 SponsorBlock 的 actionType.
    /// </summary>
    public string ActionType { get; set; } = "skip";

    /// <summary>
    /// 分类的可读名字（借用 biliuwp-lite 的映射）.
    /// </summary>
    public string CategoryName => Category switch
    {
        "sponsor" => "赞助广告",
        "intro" => "开场动画",
        "outro" => "片尾",
        "selfpromo" => "自我推广",
        "interaction" => "三连提醒",
        "preview" => "预告回顾",
        "music_offtopic" => "非音乐部分",
        "poi_highlight" => "精彩时刻",
        "filler" => "填充内容",
        "ai" => "AI 识别",
        "cloud" => "云端数据",
        _ => Category,
    };

    /// <summary>
    /// 时长.
    /// </summary>
    public double Duration => Math.Max(0, End - Start);
}

/// <summary>
/// SponsorBlock 兼容服务（小电视空降助手 / SponsorBlock 镜像）.
/// 实现方式参考 hanydd/BilibiliSponsorBlock：
/// 1. 先用哈希前缀接口 <c>/api/skipSegments/{sha256(bvid)[..4]}</c> 拉同前缀的所有视频再按 videoID 过滤；
/// 2. 老服务不支持前缀接口时退回 <c>/api/skipSegments?videoID=</c>；
/// 3. 按 category + actionType 过滤，并做会话内缓存（同一 bvid 5 分钟内不重复请求）。
/// </summary>
public static class SponsorBlockService
{
    /// <summary>
    /// 官方实例（只含 YouTube 视频，B 站视频这里查不到，作为兜底）.
    /// </summary>
    public const string OfficialServer = "https://sponsor.ajay.app";

    /// <summary>
    /// 小电视空降助手镜像（B 站专用）.
    /// </summary>
    public const string MirrorServer = "https://bsbsb.top";

    /// <summary>
    /// 自建 SponsorBlock 兼容镜像（B 站数据，实测可用的兜底）.
    /// </summary>
    public const string FallbackServer = "http://103.236.70.57:9876";

    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<string, (DateTime Time, IReadOnlyList<SponsorSegment> Segments)> Cache = new();
    private static readonly ConcurrentDictionary<string, string> BvidCache = new();

    /// <summary>
    /// 默认服务器：优先小电视镜像，其次自建兜底，最后官方.
    /// </summary>
    public static string DefaultServer => FallbackServer;

    /// <summary>
    /// 当前配置的服务器.
    /// </summary>
    public static string CurrentServer
    {
        get
        {
            var server = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockServer, DefaultServer);
            var normalized = NormalizeServer(server);
            return string.IsNullOrWhiteSpace(normalized) ? DefaultServer : normalized;
        }
    }

    /// <summary>
    /// 依次尝试的服务器列表：配置项优先，其余按镜像顺序补齐.
    /// </summary>
    public static IReadOnlyList<string> Servers
    {
        get
        {
            var list = new List<string>();
            void Add(string value)
            {
                var normalized = NormalizeServer(value);
                if (!string.IsNullOrWhiteSpace(normalized) && !list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(normalized);
                }
            }

            // 实测可用性排序：自建镜像最稳，其次用户选定的，再往后是已失效的镜像与官方.
            Add(FallbackServer);
            Add(CurrentServer);
            Add(MirrorServer);
            Add(OfficialServer);
            return list;
        }
    }

    /// <summary>
    /// 把 AID 解析成 BVID：SponsorBlock 用小电视的 BV 号做 videoID，
    /// 而客户端拿到的快照里只有 aid（114929466089407 这种）.
    /// </summary>
    /// <param name="id">aid 或已经拿到的 BV 号.</param>
    /// <param name="logger">日志.</param>
    public static async Task<string?> ResolveBvidAsync(string id, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var trimmed = id.Trim();
        if (trimmed.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (BvidCache.TryGetValue(trimmed, out var cached))
        {
            return cached;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/web-interface/view?aid={WebUtility.UrlEncode(trimmed)}");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("aid 转 bvid 失败：HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("bvid", out var bvidNode))
            {
                var bvid = bvidNode.GetString();
                if (!string.IsNullOrWhiteSpace(bvid))
                {
                    BvidCache[trimmed] = bvid;
                    logger?.LogInformation("aid {Aid} -> bvid {Bvid}", trimmed, bvid);
                    return bvid;
                }
            }

            logger?.LogWarning("aid 转 bvid 返回里没有 bvid 字段");
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "aid 转 bvid 异常");
            return null;
        }
    }

    /// <summary>
    /// 清空会话缓存.
    /// </summary>
    public static void ClearCache()
    {
        Cache.Clear();
        BvidCache.Clear();
    }
    /// <summary>
    /// 取指定视频的跳过片段.
    /// </summary>
    /// <param name="videoId">视频 BVID.</param>
    public static async Task<IReadOnlyList<SponsorSegment>> GetSegmentsAsync(string videoId)
        => await GetSegmentsAsync(videoId, null);

    /// <summary>
    /// 取指定视频的跳过片段（带日志）.
    /// </summary>
    /// <param name="videoId">视频 BVID.</param>
    /// <param name="logger">日志.</param>
    public static async Task<IReadOnlyList<SponsorSegment>> GetSegmentsAsync(string videoId, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return Array.Empty<SponsorSegment>();
        }

        if (Cache.TryGetValue(videoId, out var cached) && DateTime.Now - cached.Time < TimeSpan.FromMinutes(5))
        {
            logger?.LogInformation("空降助手命中缓存：{VideoId} {Count} 段", videoId, cached.Segments.Count);
            return cached.Segments;
        }

        var categories = BuildCategoryArray();
        var actionTypes = BuildActionTypeArray();
        var prefix = ComputeHashPrefix(videoId);
        foreach (var server in Servers)
        {
            // 1. 前缀接口（小电视空降助手的主路径）.
            var byPrefix = await TryQueryPrefixAsync(server, prefix, videoId, categories, actionTypes, logger);
            if (byPrefix is { Count: > 0 })
            {
                Cache[videoId] = (DateTime.Now, byPrefix);
                return byPrefix;
            }

            // 2. 前缀接口没记录时，再用 videoID 直查（有些镜像只实现了这一种）.
            var byVideoId = await TryQueryVideoAsync(server, videoId, categories, actionTypes, logger);
            if (byVideoId is { Count: > 0 })
            {
                Cache[videoId] = (DateTime.Now, byVideoId);
                return byVideoId;
            }
        }

        logger?.LogWarning("空降助手：所有服务器都没拿到片段（{VideoId}）", videoId);
        Cache[videoId] = (DateTime.Now, Array.Empty<SponsorSegment>());
        return Array.Empty<SponsorSegment>();
    }

    /// <summary>
    /// 计算 SponsorBlock 用的哈希前缀：sha256(bvid) 的十六进制前 4 位.
    /// </summary>
    /// <param name="videoId">BVID.</param>
    public static string ComputeHashPrefix(string videoId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(videoId));
        var builder = new StringBuilder(64);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString(0, 4);
    }

    private static async Task<IReadOnlyList<SponsorSegment>?> TryQueryPrefixAsync(
        string server,
        string prefix,
        string videoId,
        string categories,
        string actionTypes,
        ILogger? logger)
    {
        var url = $"{server}/api/skipSegments/{prefix}?categories={WebUtility.UrlEncode(categories)}&actionTypes={WebUtility.UrlEncode(actionTypes)}";
        var content = await TryGetAsync(url, logger, server, "prefix");
        if (content is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                // 前缀接口返回的是「同前缀视频」列表，需要自己挑出当前 bvid.
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("videoID", out var idNode) &&
                    string.Equals(idNode.GetString(), videoId, StringComparison.OrdinalIgnoreCase))
                {
                    var segments = ParseSegments(entry, logger);
                    logger?.LogInformation("空降助手（{Server} 前缀接口）拿到 {Count} 段", server, segments.Count);
                    return segments;
                }
            }

            logger?.LogInformation("空降助手（{Server} 前缀接口）没有该视频的记录", server);
            return Array.Empty<SponsorSegment>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<SponsorSegment>?> TryQueryVideoAsync(
        string server,
        string videoId,
        string categories,
        string actionTypes,
        ILogger? logger)
    {
        var url = $"{server}/api/skipSegments?videoID={WebUtility.UrlEncode(videoId)}&categories={WebUtility.UrlEncode(categories)}&actionTypes={WebUtility.UrlEncode(actionTypes)}";
        var content = await TryGetAsync(url, logger, server, "videoID");
        if (content is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            IReadOnlyList<SponsorSegment> segments;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // 查询接口直接返回片段数组，元素可能是 {segment,category,actionType} 或 {videoID,segments}.
                var list = new List<SponsorSegment>();
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (entry.TryGetProperty("segments", out _))
                    {
                        if (entry.TryGetProperty("videoID", out var idNode) &&
                            !string.Equals(idNode.GetString(), videoId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        list.AddRange(ParseSegments(entry, logger));
                    }
                    else
                    {
                        var single = ParseSegment(entry);
                        if (single is not null)
                        {
                            list.Add(single);
                        }
                    }
                }

                segments = list;
            }
            else
            {
                segments = Array.Empty<SponsorSegment>();
            }

            logger?.LogInformation("空降助手（{Server} videoID 接口）拿到 {Count} 段", server, segments.Count);
            return segments;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> TryGetAsync(string url, ILogger? logger, string server, string mode)
    {
        try
        {
            Debug.WriteLine($"[SponsorBlock] {mode} {url}");
            using var response = await Client.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger?.LogDebug("空降助手 {Server}（{Mode}）返回 404", server, mode);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger?.LogDebug("空降助手 {Server}（{Mode}）返回 {Status}", server, mode, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            // 这一台不通（镜像掐连接、域名解析不了、TLS 握手失败）就换下一台.
            logger?.LogDebug(ex, "空降助手 {Server}（{Mode}）请求失败", server, mode);
            return null;
        }
    }

    private static IReadOnlyList<SponsorSegment> ParseSegments(JsonElement entry, ILogger? logger)
    {
        var minDuration = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockMinDuration, 3d);
        var enabledCategories = GetEnabledCategorySet();
        var result = new List<SponsorSegment>();
        if (!entry.TryGetProperty("segments", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            var segment = ParseSegment(item);
            if (segment is null)
            {
                continue;
            }

            if (segment.Duration < minDuration)
            {
                continue;
            }

            if (!enabledCategories.Contains(segment.Category))
            {
                continue;
            }

            result.Add(segment);
        }

        return result;
    }

    private static SponsorSegment? ParseSegment(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("segment", out var times) ||
            times.ValueKind != JsonValueKind.Array ||
            times.GetArrayLength() != 2)
        {
            return null;
        }

        var start = times[0].GetDouble();
        var end = times[1].GetDouble();
        if (end <= start)
        {
            return null;
        }

        var category = item.TryGetProperty("category", out var categoryNode) ? categoryNode.GetString() : "sponsor";
        var actionType = item.TryGetProperty("actionType", out var actionNode) ? actionNode.GetString() : "skip";
        return new SponsorSegment
        {
            Start = start,
            End = end,
            Category = string.IsNullOrWhiteSpace(category) ? "sponsor" : category,
            ActionType = string.IsNullOrWhiteSpace(actionType) ? "skip" : actionType,
            Source = "sponsorblock",
        };
    }

    private static HashSet<string> GetEnabledCategorySet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sponsor" };
        if (SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeSelfPromo, true))
        {
            set.Add("selfpromo");
        }

        if (SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeIntro, true))
        {
            set.Add("intro");
        }

        if (SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeOutro, true))
        {
            set.Add("outro");
        }

        set.Add("interaction");
        set.Add("preview");
        set.Add("music_offtopic");
        set.Add("filler");
        set.Add("poi_highlight");
        return set;
    }

    private static string BuildCategoryArray() => ToJsonArray(GetEnabledCategorySet());

    /// <summary>
    /// 只跳过真正该跳的动作：skip / mute / full 都要（mute 由播放器按静音处理）.
    /// </summary>
    private static string BuildActionTypeArray() => ToJsonArray(new HashSet<string> { "skip", "mute", "full" });

    private static string ToJsonArray(IEnumerable<string> values)
        => $"[{string.Join(",", values.Select(p => $"\"{p}\""))}]";

    private static string NormalizeServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return string.Empty;
        }

        var root = server.Trim().TrimEnd('/');
        if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^4];
        }

        return root;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }
}
