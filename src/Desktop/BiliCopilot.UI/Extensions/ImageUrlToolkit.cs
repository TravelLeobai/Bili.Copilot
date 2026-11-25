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
    /// 把缩略图地址里的「裁剪」参数换成「只限宽」，让服务端按原图比例缩放。
    /// B 站接口给的动态图片常常自带 <c>@240w_240h_1c.jpg</c> 这种方形裁剪参数，
    /// 不换掉的话缩略图永远是个正方形，看不出原图是横是竖.
    /// </summary>
    /// <param name="source">原 Source（Uri 或 string）.</param>
    /// <param name="width">目标像素宽度.</param>
    /// <returns>处理后的 Source；无需处理时返回原对象.</returns>
    internal static object? BuildAspectPreservingSource(object? source, double width)
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

        var rewritten = BuildAspectPreservingUrl(raw, width);
        if (string.IsNullOrEmpty(rewritten) || string.Equals(rewritten, raw, StringComparison.Ordinal))
        {
            return source;
        }

        return source is Uri ? new Uri(rewritten) : rewritten;
    }

    /// <summary>
    /// 把 <c>@240w_240h_1c.jpg</c> 这类带高度的裁剪参数改写成 <c>@300w.webp</c>；
    /// 本来就只有宽度（不限高度）时原样返回.
    /// </summary>
    internal static string? BuildAspectPreservingUrl(string? url, double width)
    {
        if (string.IsNullOrWhiteSpace(url) || width <= 0)
        {
            return url;
        }

        var targetWidth = (int)Math.Ceiling(width);
        var atIndex = url.IndexOf('@', StringComparison.Ordinal);
        var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        var pathEnd = queryIndex < 0 ? url.Length : queryIndex;
        var suffix = $"@{targetWidth}w.webp";

        if (atIndex < 0 || atIndex > pathEnd)
        {
            // 没有处理参数：直接补一个只限宽的.
            return queryIndex < 0
                ? url + suffix
                : url[..queryIndex] + suffix + url[queryIndex..];
        }

        // 已有参数：只有带高度的（裁剪）才改写，只限宽的保持原样.
        var parameter = url[(atIndex + 1)..pathEnd];
        if (!parameter.Contains("h_", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return url[..atIndex] + suffix + url[pathEnd..];
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
