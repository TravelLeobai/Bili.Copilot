// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;

namespace BiliCopilot.UI.Services.Diagnostics;

/// <summary>
/// 一次弹幕「轨道重建」的分段耗时（毫秒口径）.
/// <para>
/// 本仓没有 mpv ASS 字幕轨（<c>sub-add</c> / <c>secondary-sid</c>），真实的重建流程是
/// 「缓存条目 → 渲染器弹幕对象 → 交给渲染层 → 对准当前进度 → 套样式 → 恢复」.
/// 所以这里只报本仓真实存在的阶段：<c>build</c> / <c>write</c> / <c>select</c> / <c>style</c> / <c>resume</c>；
/// 上游的 <c>add-ms</c> / <c>remove-ms</c> 在本仓没有对应动作，不做伪造.
/// </para>
/// <para>
/// <see cref="TotalMilliseconds"/> 是<b>端到端</b>：从重建请求发出（含 <c>Task.Delay</c> 与 UI 队列排队）
/// 到重建真正结束。<see cref="SegmentSumMilliseconds"/> 只是分段相加，两者不可互相冒充.
/// </para>
/// </summary>
internal sealed class DanmakuRebuildTimings
{
    /// <summary>
    /// Gets or sets 从发出重建请求到重建真正开始（含延迟与 UI 队列排队）.
    /// </summary>
    public double QueueMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets 把缓存条目转成渲染器弹幕对象的耗时.
    /// </summary>
    public double BuildMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets 把弹幕列表交给渲染层的耗时（Win2D 侧会在这一步建池）.
    /// </summary>
    public double WriteMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets 套用样式（字号 / 透明度 / 区域 / 速度 / 开关）的耗时.
    /// </summary>
    public double StyleMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets 把渲染器时间对准当前进度的耗时.
    /// </summary>
    public double SelectMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets 恢复渲染的耗时.
    /// </summary>
    public double ResumeMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets 端到端耗时（请求发出 → 重建结束）.
    /// </summary>
    public double TotalMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets 本次重建送入渲染层的弹幕条目数.
    /// </summary>
    public int Events { get; set; }

    /// <summary>
    /// Gets or sets 重建请求是否在完成前就已经过期（期间又来了新的重建请求）.
    /// </summary>
    public bool Stale { get; set; }

    /// <summary>
    /// Gets or sets 重建是否抛异常（异常会导致渲染层停在旧状态，必须能看出来）.
    /// </summary>
    public bool Failed { get; set; }

    /// <summary>
    /// Gets 分段之和. 明确<b>不是</b>端到端：不含排队、调度、渲染层内部异步.
    /// </summary>
    public double SegmentSumMilliseconds
        => BuildMilliseconds + WriteMilliseconds + StyleMilliseconds + SelectMilliseconds + ResumeMilliseconds;

    /// <summary>
    /// 取一个毫秒耗时.
    /// </summary>
    /// <param name="startedAt">起点 tick（<see cref="Stopwatch.GetTimestamp"/>）.</param>
    /// <returns>毫秒.</returns>
    public static double Elapsed(long startedAt)
        => Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}

/// <summary>
/// 弹幕重建的代号（generation）.
/// <para>
/// 本仓的重建流程没有 CancellationToken，也没有 epoch：一次重建只能靠「有没有更新的一次重建开始」
/// 来判断自己是不是过期。<b>这个代号只服务诊断</b>，不参与渲染决策，也就不改变任何显示行为.
/// </para>
/// </summary>
internal sealed class DanmakuRebuildGeneration
{
    private long _current;

    /// <summary>
    /// Gets 当前代号.
    /// </summary>
    public long Current => Volatile.Read(ref _current);

    /// <summary>
    /// 开一次新的重建，返回它的代号.
    /// </summary>
    /// <returns>代号.</returns>
    public long Begin() => Interlocked.Increment(ref _current);

    /// <summary>
    /// 这份结果是否仍然有效（期间没有更新的一次重建开始）.
    /// </summary>
    /// <param name="token">重建代号.</param>
    /// <returns>仍然有效返回 <see langword="true"/>.</returns>
    public bool IsCurrent(long token) => Volatile.Read(ref _current) == token;
}
