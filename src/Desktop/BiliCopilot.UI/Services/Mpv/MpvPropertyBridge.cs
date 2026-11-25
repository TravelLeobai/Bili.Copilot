// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core;

namespace BiliCopilot.UI.Services.Mpv;

/// <summary>
/// <see cref="IMpvPropertyBridge"/> 的真实现：用 <c>MpvClient.Handle</c> + <c>MpvNative</c> 直接读写 mpv 属性.
/// <para>
/// 读取只用「数字 / 布尔 / node 探测」三种格式，<b>刻意不用 <c>MpvNative.GetPropertyString</c></b>：
/// 本机实测（libmpv v0.41.0-1421，richasy.mpvkernel 0.0.2-preview2，2026-11-25）
/// 调一次 <c>GetPropertyString("demuxer-max-bytes")</c> 进程就以 0xC0000374（STATUS_HEAP_CORRUPTION）退出，
/// 而 <c>GetProperty(...Int64/Double/Flag...)</c> 与 <c>SetPropertyString</c> 都正常，
/// 同一次探针里 set → 读回的值也逐项对得上. 所以读数值一律走数值格式，不碰字符串通道.
/// </para>
/// </summary>
public sealed class MpvPropertyBridge : IMpvPropertyBridge
{
    private readonly MpvInteropHandle _handle;
    private readonly ILogger? _logger;

    private MpvPropertyBridge(MpvInteropHandle handle, ILogger? logger)
    {
        _handle = handle;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool CanReadNodeFields => false;

    /// <summary>
    /// 从一个 <see cref="MpvClient"/> 造桥；client 为空 / 已释放 / 句柄无效时返回 null（调用方按「不支持」处理）.
    /// </summary>
    /// <param name="client">mpv 客户端.</param>
    /// <param name="logger">日志；可空.</param>
    /// <returns>桥，或 null.</returns>
    public static MpvPropertyBridge? TryCreate(MpvClient? client, ILogger? logger = null)
    {
        if (client is null || client.IsDisposed)
        {
            return null;
        }

        var handle = client.Handle;
        if (!handle)
        {
            return null;
        }

        return new MpvPropertyBridge(handle, logger);
    }

    /// <inheritdoc/>
    public bool TrySetString(string name, string value, out string? error)
    {
        try
        {
            var result = MpvNative.SetPropertyString(_handle, name, value);
            if (result == MpvError.Success)
            {
                error = null;
                return true;
            }

            error = result.ToString();
            return false;
        }
        catch (Exception ex)
        {
            // 原生调用异常（句柄失效等）：不往外抛，按失败处理.
            error = $"{ex.GetType().Name}: {ex.Message}";
            _logger?.LogDebug(ex, "设置 mpv 属性 {Property} 时原生调用异常", name);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryGetInt64(string name, out long value)
    {
        value = 0;
        try
        {
            if (MpvNative.GetProperty(_handle, name, MpvFormat.Int64, out var node) != MpvError.Success)
            {
                return false;
            }

            value = node.IntegerValue;
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "读取 mpv 属性 {Property}（Int64）时原生调用异常", name);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryGetDouble(string name, out double value)
    {
        value = 0;
        try
        {
            if (MpvNative.GetProperty(_handle, name, MpvFormat.Double, out var node) != MpvError.Success)
            {
                return false;
            }

            value = node.DoubleValue;
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "读取 mpv 属性 {Property}（Double）时原生调用异常", name);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryGetFlag(string name, out bool value)
    {
        value = false;
        try
        {
            if (MpvNative.GetProperty(_handle, name, MpvFormat.Flag, out var node) != MpvError.Success)
            {
                return false;
            }

            value = node.Flag != 0;
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "读取 mpv 属性 {Property}（Flag）时原生调用异常", name);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryProbeNode(string name, out int entryCount)
    {
        entryCount = 0;
        try
        {
            if (MpvNative.GetProperty(_handle, name, MpvFormat.Node, out var node) != MpvError.Success)
            {
                return false;
            }

            // MpvNodeList 在本绑定里只有 Num 一个字段（没有 Keys/Values），所以只能数条目，读不到字段值.
            entryCount = node.Format is MpvFormat.NodeMap or MpvFormat.NodeArray
                ? node.RemoteNodeListValue.Num
                : 0;
            return node.Format is MpvFormat.NodeMap or MpvFormat.NodeArray;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "探测 mpv node 属性 {Property} 时原生调用异常", name);
            return false;
        }
    }
}
