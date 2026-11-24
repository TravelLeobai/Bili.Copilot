// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 极简 protobuf 读取器.
/// 直播新版消息（INTERACT_WORD_V2 等）把用户信息放进 pb 字段，
/// 这里只做「按需取出可读字符串」，不依赖具体描述符.
/// </summary>
internal static class LiveProtobufHelper
{
    /// <summary>
    /// 从 base64 的 pb 字段里取出所有可读字符串（按出现顺序）.
    /// </summary>
    public static IReadOnlyList<string> ExtractStrings(string base64)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return Array.Empty<string>();
        }

        // 一条进场消息里可读字段通常就 2~3 个，预开 4 个位置少一次扩容.
        var result = new List<string>(4);
        ReadStrings(bytes, result);
        return result;
    }

    private static void ReadStrings(ReadOnlySpan<byte> data, List<string> sink)
    {
        // 原来每个 length-delimited 字段都 new byte[length] + BlockCopy 一份再往下走，
        // 一条 pb 里嵌套几层就复制几份；这里全程用切片视图，一个字节数组都不分配.
        var index = 0;
        while (index < data.Length)
        {
            if (!TryReadVarint(data, ref index, out var tag))
            {
                return;
            }

            var wireType = (int)(tag & 0x7);
            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(data, ref index, out _))
                    {
                        return;
                    }

                    break;
                case 1:
                    index += 8;
                    break;
                case 2:
                    if (!TryReadVarint(data, ref index, out var length) || length < 0 || index + length > data.Length)
                    {
                        return;
                    }

                    var slice = data.Slice(index, (int)length);
                    index += (int)length;
                    if (TryDecodeText(slice, out var text))
                    {
                        sink.Add(text);
                    }
                    else
                    {
                        ReadStrings(slice, sink);
                    }

                    break;
                case 5:
                    index += 4;
                    break;
                default:
                    return;
            }

            if (index > data.Length)
            {
                return;
            }
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int index, out long value)
    {
        value = 0;
        var shift = 0;
        while (index < data.Length && shift < 64)
        {
            var current = data[index++];
            value |= (long)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    /// <summary>
    /// 判断这一片是不是可读文本；是则一并给出解码结果.
    /// 原来判读和取值各解码一次（两次 string 分配），现在只解码一次.
    /// </summary>
    private static bool TryDecodeText(ReadOnlySpan<byte> data, [NotNullWhen(true)] out string? text)
    {
        text = null;
        if (data.IsEmpty || data.Length > 512)
        {
            return false;
        }

        var hasPrintable = false;
        foreach (var value in data)
        {
            if (value < 0x20 && value is not 0x09 and not 0x0A and not 0x0D)
            {
                return false;
            }

            if (value >= 0x20)
            {
                hasPrintable = true;
            }
        }

        if (!hasPrintable)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(data);
        if (!decoded.IsNormalized())
        {
            return false;
        }

        text = decoded;
        return true;
    }
}
