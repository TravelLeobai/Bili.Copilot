// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Media.Imaging;

namespace BiliCopilot.UI.Extensions;

/// <summary>
/// 头像/小图专用的小容量解码缓存.
/// Reactor 里每次 Render 都会重建 Image 元素，不去重的话同一个头像会被反复解码.
/// 只缓存「URL + 目标像素宽度」这一种小尺寸结果，容量很小，滚动时命中的是同一批头像.
/// </summary>
internal static class SmallImageCache
{
    /// <summary>
    /// 最多缓存的小图数量（头像、表情这类）。
    /// </summary>
    private const int Capacity = 192;

    private static readonly object SyncRoot = new();

    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Lookup = new(StringComparer.Ordinal);

    private static readonly LinkedList<CacheEntry> Order = new();

    /// <summary>
    /// 取一张按目标像素解码好的图片，没有就解码并放进缓存.
    /// </summary>
    /// <param name="url">图片地址.</param>
    /// <param name="displaySize">显示边长（逻辑像素），内部按 2 倍取像素.</param>
    /// <param name="square">是否按正方形解码（头像为 true）.</param>
    /// <returns>解码好的图片；地址无效时返回 null.</returns>
    internal static BitmapImage? Get(string? url, double displaySize, bool square = true)
    {
        if (string.IsNullOrWhiteSpace(url) || displaySize <= 0)
        {
            return null;
        }

        var pixels = Math.Clamp((int)Math.Ceiling(displaySize * 2), 16, 1024);
        var key = string.Concat(url, "|", pixels.ToString(), square ? "s" : "w");

        lock (SyncRoot)
        {
            if (Lookup.TryGetValue(key, out var node))
            {
                Order.Remove(node);
                Order.AddFirst(node);
                return node.Value.Image;
            }
        }

        var target = square
            ? ImageUrlToolkit.BuildThumbnailUrl(url, pixels, pixels)
            : ImageUrlToolkit.BuildThumbnailUrl(url, pixels);

        if (string.IsNullOrWhiteSpace(target) || !Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var bitmap = new BitmapImage();
        try
        {
            bitmap.UriSource = uri;

            // 只约束一个方向，保持原图宽高比，避免拉伸.
            bitmap.DecodePixelWidth = pixels;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "小图解码失败：{Url}", target);
            return null;
        }

        lock (SyncRoot)
        {
            if (Lookup.ContainsKey(key))
            {
                return bitmap;
            }

            var node = Order.AddFirst(new CacheEntry(key, bitmap));
            Lookup[key] = node;

            while (Order.Count > Capacity)
            {
                var last = Order.Last;
                if (last is null)
                {
                    break;
                }

                Order.RemoveLast();
                Lookup.Remove(last.Value.Key);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// 系统内存压力上升时把缓存清掉，别跟页面抢内存.
    /// </summary>
    static SmallImageCache()
    {
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += (_, _) => Trim();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "订阅内存压力事件失败");
        }
    }

    /// <summary>
    /// 当前缓存了多少张小图.
    /// </summary>
    internal static int Count
    {
        get
        {
            lock (SyncRoot)
            {
                return Order.Count;
            }
        }
    }

    /// <summary>
    /// 清空缓存.
    /// </summary>
    internal static void Trim()
    {
        int removed;
        lock (SyncRoot)
        {
            removed = Order.Count;
            Lookup.Clear();
            Order.Clear();
        }

        if (removed > 0)
        {
            Serilog.Log.Information("图片缓存已按内存压力清空：{Count} 张", removed);
        }
    }

    private readonly record struct CacheEntry(string Key, BitmapImage Image);
}
