// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text.Json;

namespace BiliCopilot.UI.Services;

/// <summary>
/// SponsorBlock 片段信息.
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
    /// 片段分类.
    /// </summary>
    public string Category { get; set; } = string.Empty;
}

/// <summary>
/// SponsorBlock 服务，用于获取视频中的赞助/片头/片尾等片段.
/// 移植自 BiliLite 的 SponsorBlock 实现.
/// </summary>
public static class SponsorBlockService
{
    private const string Categories = "[\"sponsor\",\"selfpromo\",\"interaction\",\"intro\",\"outro\",\"preview\",\"music_offtopic\",\"filler\"]";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 获取指定视频的跳过片段.
    /// </summary>
    /// <param name="videoId">视频 ID（bvid 或 aid）.</param>
    /// <returns>片段列表，没有片段时返回空集合.</returns>
    public static async Task<IReadOnlyList<SponsorSegment>> GetSegmentsAsync(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return Array.Empty<SponsorSegment>();
        }

        var url = $"https://sponsor.ajay.app/api/skipSegments?videoID={WebUtility.UrlEncode(videoId)}&categories={WebUtility.UrlEncode(Categories)}";
        try
        {
            using var response = await Client.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Array.Empty<SponsorSegment>();
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var items = JsonSerializer.Deserialize<List<SegmentResponse>>(content, JsonOptions);
            if (items is null)
            {
                return Array.Empty<SponsorSegment>();
            }

            var segments = new List<SponsorSegment>();
            foreach (var item in items)
            {
                if (item.Segment is { Length: 2 } && item.Segment[1] > item.Segment[0])
                {
                    segments.Add(new SponsorSegment
                    {
                        Start = item.Segment[0],
                        End = item.Segment[1],
                        Category = item.Category ?? string.Empty,
                    });
                }
            }

            return segments;
        }
        catch (Exception)
        {
            return Array.Empty<SponsorSegment>();
        }
    }

    private sealed class SegmentResponse
    {
        public double[]? Segment { get; set; }

        public string? Category { get; set; }
    }
}
