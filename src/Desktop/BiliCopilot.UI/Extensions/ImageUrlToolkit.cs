// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Extensions;

/// <summary>
/// 图片地址工具：给 B 站图床补上服务端缩略参数，避免列表里下载并解码原图.
/// 形如 <c>https://i0.hdslb.com/bfs/archive/xxx.jpg@480w_270h_1c.webp</c>.
/// </summary>
internal static class ImageUrlToolkit
{
    /// <summary>
    /// 支持 <c>@w_h</c> 缩略参数的图床域名（含子域）.
    /// </summary>
    private static readonly string[] ThumbnailHosts =
    [
        "hdslb.com",
        "biliimg.com",
        "bilivideo.com",
        "bilibili.com",
    ];

    /// <summary>
    /// 把原图地址补成指定宽度的缩略图地址；不支持或已经是缩略图时原样返回.
    /// </summary>
    /// <param name="url">原始地址.</param>
    /// <param name="width">目标像素宽度（逻辑像素，调用方自己按需要放大）.</param>
    /// <param name="height">目标像素高度，0 表示不限.</param>
    /// <returns>处理后的地址.</returns>
    internal static string? BuildThumbnailUrl(string? url, double width, double height = 0)
    {
        if (string.IsNullOrWhiteSpace(url) || width <= 0)
        {
            return url;
        }

        // 已经带处理参数（@ 后面跟尺寸/格式）就不再叠加.
        if (url.Contains('@', StringComparison.Ordinal))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (!IsThumbnailHost(uri.Host))
        {
            return url;
        }

        // 动图（gif/webp 动图）交给服务端自己处理，别套参数.
        var path = uri.AbsolutePath;
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var targetWidth = (int)Math.Ceiling(width);
        var suffix = height > 0
            ? $"@{targetWidth}w_{(int)Math.Ceiling(height)}h_1c.webp"
            : $"@{targetWidth}w.webp";

        // 参数要贴在路径末尾、查询串之前.
        var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        return queryIndex < 0
            ? url + suffix
            : url[..queryIndex] + suffix + url[queryIndex..];
    }

    /// <summary>
    /// 把任意 Source（Uri 或 string）补成缩略图，类型保持不变.
    /// </summary>
    /// <param name="source">原 Source.</param>
    /// <param name="width">目标像素宽度.</param>
    /// <param name="height">目标像素高度.</param>
    /// <returns>处理后的 Source；无需处理时返回原对象.</returns>
    internal static object? BuildThumbnailSource(object? source, double width, double height = 0)
    {
        if (source is null || width <= 0)
        {
            return source;
        }

        var raw = source switch
        {
            Uri uri => uri.OriginalString,
            string text => text,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            return source;
        }

        var target = BuildThumbnailUrl(raw, width, height);
        if (string.IsNullOrEmpty(target) || string.Equals(target, raw, StringComparison.Ordinal))
        {
            return source;
        }

        if (source is Uri && Uri.TryCreate(target, UriKind.Absolute, out var thumbUri))
        {
            return thumbUri;
        }

        return target;
    }

    private static bool IsThumbnailHost(string host)
        => ThumbnailHosts.Any(suffix =>
            host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
