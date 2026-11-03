// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using Microsoft.Extensions.Logging;
using BiliCopilot.UI.Toolkits;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 云端广告数据服务.
/// 参考 bilijump-ai 的云端缓存思路：按 bvid 查询社区维护的广告时间点。
/// 接口地址由用户自行配置（自建 Worker / 自建服务），不内置他人凭据。
/// </summary>
public static class AdCloudService
{
    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// 是否启用云端广告数据.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.AdCloudEnabled, false);

    /// <summary>
    /// 云端接口地址，支持 {bvid} 占位符；不写占位符时会自动拼接 ?bvid=.
    /// </summary>
    public static string Endpoint
        => SettingsToolkit.ReadLocalSetting(SettingNames.AdCloudUrl, string.Empty);

    /// <summary>
    /// 拉取指定视频的广告片段.
    /// </summary>
    public static async Task<IReadOnlyList<SponsorSegment>> GetSegmentsAsync(string bvid, ILogger? logger = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(bvid))
        {
            return Array.Empty<SponsorSegment>();
        }

        var endpoint = Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            logger?.LogDebug("云端广告数据已启用，但未配置接口地址");
            return Array.Empty<SponsorSegment>();
        }

        var url = endpoint.Contains("{bvid}", StringComparison.OrdinalIgnoreCase)
            ? endpoint.Replace("{bvid}", WebUtility.UrlEncode(bvid), StringComparison.OrdinalIgnoreCase)
            : $"{endpoint.TrimEnd('/')}{(endpoint.Contains('?') ? "&" : "?")}bvid={WebUtility.UrlEncode(bvid)}";

        try
        {
            using var response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<SponsorSegment>();
            }

            var content = await response.Content.ReadAsStringAsync();
            return ParseSegments(content);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "云端广告数据请求失败");
            return Array.Empty<SponsorSegment>();
        }
    }

    private static IReadOnlyList<SponsorSegment> ParseSegments(string content)
    {
        var result = new List<SponsorSegment>();
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            // 兼容 { data: [...] } / { ads: [...] } / 直接数组.
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data))
                {
                    root = data;
                }
                else if (root.TryGetProperty("ads", out var ads))
                {
                    root = ads;
                }
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in root.EnumerateArray())
            {
                var start = ReadDouble(item, "start") ?? ReadDouble(item, "start_time") ?? ReadDouble(item, "s");
                var end = ReadDouble(item, "end") ?? ReadDouble(item, "end_time") ?? ReadDouble(item, "e");
                if (start is null || end is null || end <= start)
                {
                    continue;
                }

                result.Add(new SponsorSegment
                {
                    Start = start.Value,
                    End = end.Value,
                    Category = "cloud",
                    Source = "cloud",
                });
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    private static double? ReadDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var node))
        {
            return null;
        }

        return node.ValueKind switch
        {
            JsonValueKind.Number => node.GetDouble(),
            JsonValueKind.String => double.TryParse(node.GetString(), out var value) ? value : null,
            _ => null,
        };
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}