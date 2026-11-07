// Copyright (c) Bili Copilot. All rights reserved.

using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Services;

/// <summary>
/// Bilijump 云端广告库（Cloudflare D1）.
/// 上游脚本按 cid 查一行的 data 字段，data 里是 {ads:[{start_time,end_time,product_name,ad_content}], msg}.
/// </summary>
internal static class BiliJumpService
{
    private const string QueryEndpoint =
        "https://api.cloudflare.com/client/v4/accounts/34c49ed8e1d2bd41c330fb65de4c5890/d1/database/c1ad567a-2375-49b4-83e2-d1de52a0902f/query";

    // 上游脚本里公开的只读令牌，跟着它一起用；对方换令牌就会失效.
    private const string Token = "Dmlpe9TkvsvBCE0N-FkqeRkN5ANCyHTnUSnAtGCH";

    private const string Sql = "SELECT data FROM bilijump WHERE cid = ? LIMIT 1;";

    private static readonly HttpClient Client = new(
        new SocketsHttpHandler { ConnectCallback = IpV4Connector.ConnectAsync })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>
    /// 这个来源是否参与查询.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.BiliJumpEnabled, true);

    /// <summary>
    /// 用 cid 查云端广告库.
    /// </summary>
    /// <param name="cid">视频分 P 的 cid.</param>
    /// <param name="logger">日志.</param>
    public static async Task<IReadOnlyList<SponsorSegment>> GetSegmentsAsync(string cid, ILogger? logger = null)
    {
        // 自检：LocalState 下放 bilijump-cid-test.txt（内容写一个 cid），
        // 查库时就用它，方便验证解析链路而不必等一条命中的视频.
        var overrideCid = ReadTestCid();
        if (!string.IsNullOrEmpty(overrideCid))
        {
            logger?.LogInformation("Bilijump：使用自检 cid {Cid} 覆盖原 cid {Origin}", overrideCid, cid);
            cid = overrideCid;
        }

        if (string.IsNullOrEmpty(cid))
        {
            return [];
        }

        try
        {
            // 这个应用全局禁用了 JSON 反射序列化，连 JsonSerializer.Serialize(string) 都会抛，
            // 所以 payload 手工拼 + 手工转义（SQL 和 cid 都不含需要转义的字符，仍然稳妥地转一遍）.
            var payload = $"{{\"sql\":\"{EscapeJson(Sql)}\",\"params\":[\"{EscapeJson(cid)}\"]}}";
            using var request = new HttpRequestMessage(HttpMethod.Post, QueryEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("Bilijump 查询失败：HTTP {Status}", (int)response.StatusCode);
                return [];
            }

            return Parse(text, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Bilijump 查询异常");
            return [];
        }
    }

    /// <summary>
    /// 把 bvid 换成第 1 个分 P 的 cid.
    /// </summary>
    public static async Task<string> ResolveCidAsync(string bvid, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(bvid))
        {
            return string.Empty;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.bilibili.com/x/player/pagelist?bvid={bvid}&jsonp=jsonp");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0
                && data[0].TryGetProperty("cid", out var cidNode))
            {
                return cidNode.ToString();
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "取 cid 失败，Bilijump 来源跳过");
        }

        return string.Empty;
    }

    private static IReadOnlyList<SponsorSegment> Parse(string text, ILogger? logger)
    {
        var result = new List<SponsorSegment>();
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var resultNode)
                || resultNode.GetArrayLength() == 0
                || !resultNode[0].TryGetProperty("results", out var rows)
                || rows.GetArrayLength() == 0
                || !rows[0].TryGetProperty("data", out var dataNode))
            {
                return result;
            }

            var dataText = dataNode.GetString();
            if (string.IsNullOrEmpty(dataText))
            {
                return result;
            }

            using var payload = JsonDocument.Parse(dataText);
            if (!payload.RootElement.TryGetProperty("ads", out var ads) || ads.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var ad in ads.EnumerateArray())
            {
                if (!TryReadSeconds(ad, "start_time", out var start)
                    || !TryReadSeconds(ad, "end_time", out var end)
                    || end <= start)
                {
                    continue;
                }

                result.Add(new SponsorSegment
                {
                    Start = start,
                    End = end,
                    Category = "sponsor",
                    Source = "bilijump",
                    ActionType = "skip",
                    Note = ReadString(ad, "product_name"),
                    Detail = ReadString(ad, "ad_content"),
                });
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "解析 Bilijump 返回失败");
        }

        return result;
    }

    private static bool TryReadSeconds(JsonElement ad, string name, out double value)
    {
        value = 0;
        if (!ad.TryGetProperty(name, out var node))
        {
            return false;
        }

        // 上游是字符串（"102.15"），也兼容数字.
        return node.ValueKind switch
        {
            JsonValueKind.String => double.TryParse(node.GetString(), out value),
            JsonValueKind.Number => node.TryGetDouble(out value),
            _ => false,
        };
    }

    private static string ReadString(JsonElement ad, string name)
        => ad.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ReadTestCid()
    {
        try
        {
            var path = Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                "bilijump-cid-test.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}


