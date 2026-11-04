// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// B 站表情包（含装扮表情）.
/// 评论正文里的表情是 <c>[名字]</c> 占位文本，真正的图片要另外取：
/// 面板接口 <c>/x/emote/user/panel/web?business=reply</c> 会返回当前账号可用的
/// 全部表情（普通表情 + 已购装扮表情），把 text 和 url 对起来就能把占位换成图.
/// </summary>
internal static class BiliEmoteService
{
    private const string PanelApi = "https://api.bilibili.com/x/emote/user/panel/web?business=reply";

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = IpV4Connector.ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static readonly Dictionary<string, BiliEmote> Cache = new(StringComparer.Ordinal);
    private static DateTimeOffset _lastSync = DateTimeOffset.MinValue;
    private static bool _syncing;

    /// <summary>
    /// 是否已经拿到表情.
    /// </summary>
    public static bool IsReady => Cache.Count > 0;

    /// <summary>
    /// 按占位文本取表情.
    /// </summary>
    /// <param name="token">形如 <c>[名字]</c> 的占位文本.</param>
    public static BiliEmote? TryGet(string token)
        => Cache.TryGetValue(token, out var emote) ? emote : null;

    /// <summary>
    /// 确保表情已经加载（24 小时内只拉一次）.
    /// </summary>
    /// <param name="cookie">登录态，装扮表情需要它.</param>
    /// <param name="logger">日志.</param>
    public static async Task EnsureLoadedAsync(string cookie, ILogger? logger = null)
    {
        if (_syncing)
        {
            return;
        }

        if (Cache.Count > 0 && DateTimeOffset.Now - _lastSync < TimeSpan.FromHours(24))
        {
            return;
        }

        if (TryLoadFromCache())
        {
            return;
        }

        _syncing = true;
        try
        {
            logger?.LogInformation("表情面板：开始加载（cookie 长度 {Length}）", cookie?.Length ?? 0);
            using var request = new HttpRequestMessage(HttpMethod.Get, PanelApi);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("表情面板请求失败：HTTP {Status}", (int)response.StatusCode);
                return;
            }

            var count = ParsePanel(text);
            logger?.LogInformation("表情面板已加载：{Count} 个", count);
            if (count == 0)
            {
                logger?.LogWarning("表情面板为空，可能是未登录或接口结构变了：{Body}", text.Length > 160 ? text[..160] : text);
            }
            if (count > 0)
            {
                _lastSync = DateTimeOffset.Now;
                SaveToCache();
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "加载表情面板失败");
        }
        finally
        {
            _syncing = false;
        }
    }

    private static int ParsePanel(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("packages", out var packages)
                || packages.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            Cache.Clear();
            foreach (var package in packages.EnumerateArray())
            {
                if (!package.TryGetProperty("emote", out var emotes) || emotes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var emote in emotes.EnumerateArray())
                {
                    var token = emote.TryGetProperty("text", out var textNode) ? textNode.GetString() : null;
                    var url = emote.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null;
                    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var size = 1;
                    if (emote.TryGetProperty("meta", out var meta)
                        && meta.TryGetProperty("size", out var sizeNode)
                        && sizeNode.TryGetInt32(out var parsed))
                    {
                        size = parsed;
                    }

                    Cache[token] = new BiliEmote(token, url, size);
                }
            }

            return Cache.Count;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 往缓存里补一个表情（来自评论原文的 content.emote）.
    /// </summary>
    /// <param name="token">占位文本.</param>
    /// <param name="url">图片地址.</param>
    /// <param name="size">尺寸档位.</param>
    /// <returns>是否新增.</returns>
    public static bool AddDynamic(string token, string url, int size)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(url) || Cache.ContainsKey(token))
        {
            return false;
        }

        Cache[token] = new BiliEmote(token, url, size);
        return true;
    }

    /// <summary>
    /// 把当前缓存写回磁盘（运行期补充的表情也要留住）.
    /// </summary>
    public static void SaveCache() => SaveToCache();

    private static string CacheFile
        => Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "emote-cache.txt");

    private static bool TryLoadFromCache()
    {
        try
        {
            if (!File.Exists(CacheFile))
            {
                return false;
            }

            var lines = File.ReadAllLines(CacheFile);
            if (lines.Length < 2 || !long.TryParse(lines[0], out var stamp))
            {
                return false;
            }

            var time = DateTimeOffset.FromUnixTimeSeconds(stamp).ToLocalTime();
            if (DateTimeOffset.Now - time > TimeSpan.FromHours(24))
            {
                return false;
            }

            Cache.Clear();
            for (var i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length >= 2)
                {
                    var size = parts.Length > 2 && int.TryParse(parts[2], out var parsed) ? parsed : 1;
                    Cache[parts[0]] = new BiliEmote(parts[0], parts[1], size);
                }
            }

            _lastSync = time;
            return Cache.Count > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SaveToCache()
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(_lastSync.ToUnixTimeSeconds()).Append('\n');
            foreach (var pair in Cache)
            {
                builder.Append(pair.Value.Token).Append('|').Append(pair.Value.Url).Append('|').Append(pair.Value.Size).Append('\n');
            }

            // LocalSettings 单个值 8KB 上限，表情几百条必须落文件.
            File.WriteAllText(CacheFile, builder.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 一个表情.
/// </summary>
/// <param name="Token">占位文本.</param>
/// <param name="Url">图片地址.</param>
/// <param name="Size">尺寸档位（1 普通，2 大）.</param>
internal sealed record BiliEmote(string Token, string Url, int Size);