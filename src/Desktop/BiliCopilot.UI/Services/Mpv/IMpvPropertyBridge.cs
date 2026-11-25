// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services.Mpv;

/// <summary>
/// mpv 属性桥：直接按属性名读写 mpv 属性.
/// <para>
/// 为什么需要它：内核的 <c>MpvClient</c> 只给了几个固定 setter
/// （demuxer-max-bytes / demuxer-readahead-secs / demuxer-max-back-bytes / cache-on-disk / cache-dir），
/// <c>cache-secs</c>、<c>demuxer-donate-buffer</c>、<c>demuxer-seekable-cache</c> 根本没有 setter，
/// 缓冲链路必须能下发它们，才能让设置页里的选项真的生效.
/// </para>
/// <para>
/// 约定：<b>所有方法都不抛异常</b>，失败返回 false 并给出原因，由调用方决定记日志还是降级.
/// 实现走 <c>MpvClient.Handle</c> + <c>MpvNative</c>，在 mpv 事件线程之外调用是安全的（mpv 属性接口本身线程安全）.
/// </para>
/// </summary>
public interface IMpvPropertyBridge
{
    /// <summary>
    /// 本绑定能不能直接读出 node 属性的字段.
    /// 实测结论：<c>false</c> —— 内核的 <c>MpvNodeList</c> 只映射了 <c>Num</c> 字段，
    /// 没有 Keys/Values，所以 <c>demuxer-cache-state</c> 只能确认「可读、是 NodeMap、有几个键」，
    /// 具体数值要靠内核已经解析好的 <c>MpvClient.CacheStateChanged</c> 事件.
    /// </summary>
    bool CanReadNodeFields { get; }

    /// <summary>
    /// 设置一个属性（字符串形式，mpv 会按属性自身类型解析）.
    /// </summary>
    /// <param name="name">属性名.</param>
    /// <param name="value">属性值文本，例如 "300MiB"、"0"、"yes"、"no".</param>
    /// <param name="error">失败时 mpv 返回的错误名（例如 PropertyFormat、PropertyNotFound）；成功时为 null.</param>
    /// <returns>是否成功.</returns>
    bool TrySetString(string name, string value, out string? error);

    /// <summary>
    /// 按整数读属性（不会分配内存，安全）.
    /// </summary>
    /// <param name="name">属性名.</param>
    /// <param name="value">读到的值.</param>
    /// <returns>是否读到.</returns>
    bool TryGetInt64(string name, out long value);

    /// <summary>
    /// 按浮点读属性（不会分配内存，安全）.
    /// </summary>
    /// <param name="name">属性名.</param>
    /// <param name="value">读到的值.</param>
    /// <returns>是否读到.</returns>
    bool TryGetDouble(string name, out double value);

    /// <summary>
    /// 按布尔读属性.
    /// </summary>
    /// <param name="name">属性名.</param>
    /// <param name="value">读到的值.</param>
    /// <returns>是否读到.</returns>
    bool TryGetFlag(string name, out bool value);

    /// <summary>
    /// 探测一个 node 属性：能读到就返回 true，并给出条目数.
    /// 字段值读不出来（见 <see cref="CanReadNodeFields"/>），所以这个方法只用于确认「属性存在且是 NodeMap」.
    /// </summary>
    /// <param name="name">属性名.</param>
    /// <param name="entryCount">节点里的条目数.</param>
    /// <returns>是否读得到.</returns>
    bool TryProbeNode(string name, out int entryCount);
}
