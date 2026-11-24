// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BiliCopilot.UI.Converters;

/// <summary>
/// 将链接转换为位图.
/// 绑定每次求值都会走 <see cref="Convert"/>，所以这里按「地址 + 解码宽度」复用同一张
/// <see cref="BitmapImage"/>：原来每求值一次就 new 一张，等于把原图重新解码一遍.
/// 另支持 <c>ConverterParameter</c> 传目标像素宽度，用来限制解码尺寸（只缩小不放大）.
/// </summary>
internal sealed partial class UriToBitmapConverter : IValueConverter
{
    /// <summary>
    /// 缓存条数上限，超出按 LRU 淘汰；内存压力上升时整表清掉.
    /// </summary>
    private const int Capacity = 24;

    private const int MinDecodePixelWidth = 16;
    private const int MaxDecodePixelWidth = 4096;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Lookup = new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> Order = new();

    static UriToBitmapConverter()
    {
        try
        {
            // 和 SmallImageCache 一样：系统内存吃紧时别攥着位图不放.
            Windows.System.MemoryManager.AppMemoryUsageIncreased += (_, _) => Trim();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "订阅内存压力事件失败（UriToBitmapConverter）");
        }
    }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var uri = value switch
        {
            string url => Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var parsed) ? parsed : null,
            Uri target => target,
            _ => null,
        };

        if (uri is null)
        {
            return default;
        }

        var decodeWidth = ResolveDecodeWidth(parameter);
        var key = string.Concat(uri.OriginalString, "|", decodeWidth.ToString());

        lock (SyncRoot)
        {
            if (Lookup.TryGetValue(key, out var node))
            {
                Order.Remove(node);
                Order.AddFirst(node);
                return node.Value.Image;
            }
        }

        var bitmap = new BitmapImage();

        // 先给解码尺寸、再给地址：地址一设，解码就按原图尺寸排队；这里必须写在前面.
        if (decodeWidth > 0)
        {
            bitmap.DecodePixelWidth = decodeWidth;
        }

        bitmap.UriSource = uri;

        lock (SyncRoot)
        {
            if (Lookup.TryGetValue(key, out var existing))
            {
                return existing.Value.Image;
            }

            var added = Order.AddFirst(new CacheEntry(key, bitmap));
            Lookup[key] = added;

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

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();

    /// <summary>
    /// 清空缓存，交给内存压力回调与调试使用.
    /// </summary>
    internal static void Trim()
    {
        lock (SyncRoot)
        {
            Lookup.Clear();
            Order.Clear();
        }
    }

    /// <summary>
    /// 解析 ConverterParameter：数字（或数字 + px）即目标像素宽，没给就不限制.
    /// </summary>
    private static int ResolveDecodeWidth(object parameter)
    {
        var raw = parameter switch
        {
            int width => width,
            double dwidth => (int)dwidth,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

        return raw <= 0 ? 0 : Math.Clamp(raw, MinDecodePixelWidth, MaxDecodePixelWidth);
    }

    private readonly record struct CacheEntry(string Key, BitmapImage Image);
}
