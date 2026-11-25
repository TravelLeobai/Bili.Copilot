// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.MpvCacheOptions.Tests;

using BiliCopilot.UI.Services.Mpv;

/// <summary>
/// 假的属性桥：只把「下发了什么」记下来，并按需返回读取结果.
/// 不启动 mpv、不碰原生库.
/// </summary>
internal sealed class RecordingMpvPropertyBridge : IMpvPropertyBridge
{
    private readonly Dictionary<string, double> _doubles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _integers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failingProperties = new(StringComparer.Ordinal);

    /// <summary>所有下发记录，按调用顺序.</summary>
    public List<(string Property, string Value)> Sets { get; } = [];

    /// <inheritdoc/>
    public bool CanReadNodeFields => false;

    /// <summary>让某个属性下发失败，用来验证失败也会被记录.</summary>
    /// <param name="property">属性名.</param>
    public void FailOn(string property) => _failingProperties.Add(property);

    /// <summary>预置一个可读的整数属性.</summary>
    /// <param name="property">属性名.</param>
    /// <param name="value">值.</param>
    public void SetIntegerReadable(string property, long value) => _integers[property] = value;

    /// <summary>预置一个可读的浮点属性.</summary>
    /// <param name="property">属性名.</param>
    /// <param name="value">值.</param>
    public void SetDoubleReadable(string property, double value) => _doubles[property] = value;

    /// <inheritdoc/>
    public bool TrySetString(string name, string value, out string? error)
    {
        Sets.Add((name, value));
        if (_failingProperties.Contains(name))
        {
            error = "PropertyFormat";
            return false;
        }

        error = null;
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetInt64(string name, out long value) => _integers.TryGetValue(name, out value);

    /// <inheritdoc/>
    public bool TryGetDouble(string name, out double value) => _doubles.TryGetValue(name, out value);

    /// <inheritdoc/>
    public bool TryGetFlag(string name, out bool value)
    {
        value = false;
        return false;
    }

    /// <inheritdoc/>
    public bool TryProbeNode(string name, out int entryCount)
    {
        entryCount = 0;
        return false;
    }

    /// <summary>
    /// 某个属性被下发的所有值（用于验证「复用会重新下发」）.
    /// </summary>
    /// <param name="property">属性名.</param>
    /// <returns>值列表.</returns>
    public IReadOnlyList<string> ValuesOf(string property)
        => Sets.Where(set => set.Property == property).Select(set => set.Value).ToList();
}
