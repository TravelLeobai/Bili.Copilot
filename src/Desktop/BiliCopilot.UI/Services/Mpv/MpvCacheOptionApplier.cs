// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services.Mpv;

/// <summary>一条缓冲参数的下发结果.</summary>
public enum MpvCacheAttemptState
{
    /// <summary>已下发.</summary>
    Sent,

    /// <summary>下发失败（mpv 返回错误或原生调用异常）.</summary>
    Failed,
}

/// <summary>一条缓冲参数的下发结果.</summary>
/// <param name="Property">属性名.</param>
/// <param name="Value">下发的值.</param>
/// <param name="State">成功还是失败.</param>
/// <param name="Error">失败原因（mpv 错误名）；成功为 null.</param>
public sealed record MpvCacheOptionAttempt(string Property, string Value, MpvCacheAttemptState State, string? Error);

/// <summary>
/// 一次下发的结果：调用方按 <see cref="Attempts"/> 逐条写日志，
/// 按 <see cref="SkippedAuto"/> 说明哪些项因为选了「自动」而没下发.
/// </summary>
/// <param name="Attempts">逐条结果，顺序与下发顺序一致.</param>
/// <param name="SkippedAuto">因「自动」跳过的属性名.</param>
public sealed record MpvCacheApplyResult(IReadOnlyList<MpvCacheOptionAttempt> Attempts, IReadOnlyList<string> SkippedAuto)
{
    /// <summary>成功下发的条数.</summary>
    public int SentCount => Attempts.Count(attempt => attempt.State == MpvCacheAttemptState.Sent);

    /// <summary>失败的条数.</summary>
    public int FailedCount => Attempts.Count(attempt => attempt.State == MpvCacheAttemptState.Failed);

    /// <summary>是不是全部成功（「自动」跳过不算失败）.</summary>
    public bool AllSucceeded => FailedCount == 0;

    /// <summary>失败的条目.</summary>
    public IReadOnlyList<MpvCacheOptionAttempt> Failures
        => Attempts.Where(attempt => attempt.State == MpvCacheAttemptState.Failed).ToList();
}

/// <summary>
/// 把缓冲下发计划逐条打到 mpv 上.
/// <para>
/// 刻意<b>不缓存「已下发」状态</b>：同一个实例连续调用两次就是两条完整下发，
/// 这正是「同窗口换集复用播放器时要重新生效」所依赖的行为.
/// </para>
/// </summary>
public sealed class MpvCacheOptionApplier
{
    private readonly IMpvPropertyBridge _bridge;

    /// <summary>
    /// Initializes a new instance of the <see cref="MpvCacheOptionApplier"/> class.
    /// </summary>
    /// <param name="bridge">属性桥.</param>
    public MpvCacheOptionApplier(IMpvPropertyBridge bridge)
        => _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    /// <summary>
    /// 按设置下发一遍（内部先生成计划）.
    /// </summary>
    /// <param name="settings">设置快照.</param>
    /// <returns>下发结果.</returns>
    public MpvCacheApplyResult Apply(MpvCacheSettings settings)
        => Apply(MpvCacheOptionPlanner.Build(settings));

    /// <summary>
    /// 按计划下发一遍.
    /// </summary>
    /// <param name="plan">下发计划.</param>
    /// <returns>下发结果.</returns>
    public MpvCacheApplyResult Apply(MpvCachePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var attempts = new List<MpvCacheOptionAttempt>(plan.Options.Count);
        foreach (var option in plan.Options)
        {
            var ok = _bridge.TrySetString(option.Property, option.Value, out var error);
            attempts.Add(new MpvCacheOptionAttempt(
                option.Property,
                option.Value,
                ok ? MpvCacheAttemptState.Sent : MpvCacheAttemptState.Failed,
                ok ? null : error));
        }

        return new MpvCacheApplyResult(attempts, plan.SkippedAuto);
    }
}
