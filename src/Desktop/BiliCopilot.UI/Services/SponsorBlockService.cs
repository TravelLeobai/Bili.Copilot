// Copyright (c) Bili Copilot. All rights reserved.

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
        var server = CurrentServer;
        var url = $"{server}/api/skipSegments?videoID={WebUtility.UrlEncode(videoId)}&categories={WebUtility.UrlEncode(categories)}";
        try
        {
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
            return Array.Empty<SponsorSegment>();
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
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private sealed class SegmentResponse
    {
        public double[]? Segment { get; set; }

        public string? Category { get; set; }
    }
}