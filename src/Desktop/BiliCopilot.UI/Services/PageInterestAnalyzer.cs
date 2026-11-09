// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 收藏 / 历史 / 稍后再看这三个页面顶部的兴趣分析。
/// 取前 20 个视频的标题、UP 主、封面（字幕正文的抓取要额外请求，这里只带简介文本），
/// 交给 <see cref="AiInterestAnalyzer"/> 走同一套严格格式 + 字数上限。
/// </summary>
public static class PageInterestAnalyzer
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<string?>> Running = new();

    /// <summary>
    /// 取缓存结果（没有就返回 null，不触发请求）.
    /// </summary>
    /// <param name="pageKey">页面标识（favorites / history / viewlater）.</param>
    /// <returns>分析文本.</returns>
    public static string? GetCached(string pageKey)
        => Cache.TryGetValue(pageKey, out var value) ? value : null;

    /// <summary>
    /// 分析一页的兴趣（同一页同时只跑一次，结果缓存到应用退出）.
    /// </summary>
    /// <param name="pageKey">页面标识.</param>
    /// <param name="videos">这一页的视频（会取前 20 个）.</param>
    /// <param name="logger">日志.</param>
    /// <returns>分析文本，失败返回 null.</returns>
    public static Task<string?> AnalyzeAsync(string pageKey, IEnumerable<VideoItemViewModel> videos, ILogger? logger)
    {
        if (Running.TryGetValue(pageKey, out var running))
        {
            return running;
        }

        if (Cache.TryGetValue(pageKey, out var cached))
        {
            return Task.FromResult<string?>(cached);
        }

        var samples = videos
            .Take(20)
            .Select(p => new InterestSample(p.Title ?? string.Empty, p.Author ?? string.Empty, string.Empty, p.Cover?.ToString() ?? string.Empty))
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .ToList();

        if (samples.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var task = AnalyzeCoreAsync(pageKey, samples, logger);
        Running[pageKey] = task;
        return task;
    }

    /// <summary>
    /// 清掉缓存（页面刷新时用）.
    /// </summary>
    /// <param name="pageKey">页面标识.</param>
    public static void Invalidate(string pageKey)
    {
        Cache.TryRemove(pageKey, out _);
        Running.TryRemove(pageKey, out _);
    }

    private static async Task<string?> AnalyzeCoreAsync(string pageKey, IReadOnlyList<InterestSample> samples, ILogger? logger)
    {
        try
        {
            var text = await AiInterestAnalyzer.AnalyzeAsync(samples, logger).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                Cache[pageKey] = text;
            }

            return text;
        }
        finally
        {
            Running.TryRemove(pageKey, out _);
        }
    }
}
