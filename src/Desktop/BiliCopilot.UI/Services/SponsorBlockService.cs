// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
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
}

/// <summary>
/// SponsorBlock 服务.
/// 支持官方实例与 B 站专用镜像（如 bsbsb.top），分类与最短时长可配置.
/// </summary>
public static class SponsorBlockService
{
    private const string OfficialServer = "https://sponsor.ajay.app";
    private const string MirrorServer = "https://bsbsb.top";
    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// 默认服务器.
    /// </summary>
    public static string DefaultServer => OfficialServer;

    /// <summary>
    /// 当前服务器.
    /// </summary>
    public static string CurrentServer
    {
        get
        {
            var server = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockServer, OfficialServer);
            return string.IsNullOrWhiteSpace(server) ? OfficialServer : server.TrimEnd('/');
        }
    }

    /// <summary>
    /// 取指定视频的跳过片段.
    /// </summary>
    public static async Task<IReadOnlyList<SponsorSegment>> GetSegmentsAsync(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return Array.Empty<SponsorSegment>();
        }

        var categories = BuildCategories();
        // 先问配置的服务器，失败（TLS 握手、连接被掐）就换另一个已知实例再问一次.
        var candidates = new List<string> { CurrentServer };
        foreach (var fallback in new[] { OfficialServer, MirrorServer })
        {
            if (!candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(fallback);
            }
        }

        foreach (var server in candidates)
        {
            // 用户/迁移可能把地址填成 https://bsbsb.top/api，直接拼 /api/skipSegments 会变成 /api/api/... 打 404.
            var root = server.TrimEnd('/');
            if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                root = root[..^4];
            }

            var url = $"{root}/api/skipSegments?videoID={WebUtility.UrlEncode(videoId)}&categories={WebUtility.UrlEncode(categories)}";
            var result = await TryQueryAsync(url, server);
            if (result is not null)
            {
                return result;
            }
        }

        return Array.Empty<SponsorSegment>();
    }

    private static async Task<IReadOnlyList<SponsorSegment>?> TryQueryAsync(string url, string server)
    {
        try
        {
            Debug.WriteLine($"[SponsorBlock] 查询 {server}");
            using var response = await Client.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Array.Empty<SponsorSegment>();
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var items = JsonSerializer.Deserialize<List<SegmentResponse>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (items is null)
            {
                return Array.Empty<SponsorSegment>();
            }

            var minDuration = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockMinDuration, 3d);
            var segments = new List<SponsorSegment>();
            foreach (var item in items)
            {
                if (item.Segment is not { Length: 2 } || item.Segment[1] <= item.Segment[0])
                {
                    continue;
                }

                if (item.Segment[1] - item.Segment[0] < minDuration)
                {
                    continue;
                }

                segments.Add(new SponsorSegment
                {
                    Start = item.Segment[0],
                    End = item.Segment[1],
                    Category = item.Category ?? "sponsor",
                    Source = "sponsorblock",
                });
            }

            return segments;
        }
        catch (Exception)
        {
            // 这一台不通（常见是镜像要求 UA、官方域名 TLS 被掐），交给下一台.
            return null;
        }
    }

    private static string BuildCategories()
    {
        var list = new List<string> { "sponsor" };
        if (SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeSelfPromo, true))
        {
            list.Add("selfpromo");
        }

        if (SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeIntro, true))
        {
            list.Add("intro");
        }

        if (SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeOutro, true))
        {
            list.Add("outro");
        }

        list.Add("interaction");
        list.Add("preview");
        list.Add("music_offtopic");
        list.Add("filler");
        return $"[{string.Join(",", list.Select(p => $"\"{p}\""))}]";
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        // bsbsb.top 这类镜像会直接掐掉没有 UA 的连接.
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) BiliCopilot/2.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    private sealed class SegmentResponse
    {
        public double[]? Segment { get; set; }

        public string? Category { get; set; }
    }
}