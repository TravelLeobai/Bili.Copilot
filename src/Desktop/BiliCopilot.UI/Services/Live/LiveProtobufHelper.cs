// Copyright (c) Bili Copilot. All rights reserved.

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

        var result = new List<string>();
        ReadStrings(bytes, result);
        return result;
    }

    private static void ReadStrings(byte[] data, List<string> sink)
    {
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

                    var slice = new byte[length];
                    Buffer.BlockCopy(data, index, slice, 0, (int)length);
                    index += (int)length;
                    if (IsReadableText(slice))
                    {
                        sink.Add(Encoding.UTF8.GetString(slice));
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

    private static bool TryReadVarint(byte[] data, ref int index, out long value)
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

    private static bool IsReadableText(byte[] data)
    {
        if (data.Length is 0 or > 512)
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

        return hasPrintable && Encoding.UTF8.GetString(data).IsNormalized();
    }
}