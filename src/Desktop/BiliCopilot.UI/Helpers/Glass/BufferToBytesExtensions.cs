// Copyright (c) Bili Copilot. All rights reserved.

// 移植自 https://github.com/SuGar0218/TryLiquidGlass-WinUI3（Apache-2.0 sample），
// 只保留"把一块区域画成液态玻璃"所需的最小集合，命名空间换成本项目的.

using Windows.Storage.Streams;

namespace BiliCopilot.UI.Helpers.Glass;

/// <summary>
/// IBuffer → byte[] 的快捷转换.
/// </summary>
internal static class BufferToBytesExtensions
{
    /// <summary>
    /// 把缓冲区读成字节数组.
    /// </summary>
    /// <param name="buffer">源缓冲区.</param>
    /// <returns>字节数组.</returns>
    public static byte[] ToBytes(this IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
