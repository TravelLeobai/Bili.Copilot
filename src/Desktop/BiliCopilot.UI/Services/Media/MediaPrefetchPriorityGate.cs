// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 一张卡片作为「预取候选」时需要的信息（由视口哨兵算出来，不含任何 WinUI 类型，方便单测）.
/// </summary>
public sealed class MediaPrefetchCandidate
{
    /// <summary>
    /// 视频 id（视频是 BV 号，番剧用 epId / seasonId）.
    /// </summary>
    public string VideoId { get; init; } = string.Empty;

    /// <summary>
    /// 显示名（日志用）.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 是否是番剧.
    /// </summary>
    public bool IsPgc { get; init; }

    /// <summary>
    /// 卡片在视口内的可见面积比例（0~1）.
    /// </summary>
    public double VisibleRatio { get; init; }

    /// <summary>
    /// 鼠标是否悬停在卡片上.
    /// </summary>
    public bool IsHovered { get; init; }

    /// <summary>
    /// 键盘焦点是否在卡片上.
    /// </summary>
    public bool IsFocused { get; init; }

    /// <summary>
    /// 是否刚刚被点开（正在播放的那一张）.
    /// </summary>
    public bool IsPlaying { get; init; }

    /// <summary>
    /// 卡片在列表里的序号（用于稳定排序与「下一屏」判断）.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// 已经停留在视口里多久.
    /// </summary>
    public TimeSpan Dwell { get; init; }

    /// <summary>
    /// 是否属于「下一屏」（还没进视口，但再滚一点就到了）.
    /// </summary>
    public bool IsNextScreen { get; init; }

    /// <inheritdoc/>
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{(string.IsNullOrEmpty(Title) ? VideoId : Title)}(visible={VisibleRatio:F2},hover={IsHovered},focus={IsFocused},playing={IsPlaying},dwell={Dwell.TotalMilliseconds:F0}ms,next={IsNextScreen})");
}

/// <summary>
/// 预取优先级与防抖闸门（纯逻辑）.
/// <para>
/// 三个职责：
/// <list type="number">
/// <item>防抖：视口一有变化就 <see cref="Submit"/>，只有安静满 <c>debounce</c> 才吐出稳定快照 ——
/// 快速滚动时一路 submit 下去，直到停下才发起预取，不会边滚边下。</item>
/// <item>排序：悬停 / 焦点 / 刚点开 &gt; 可见面积 &gt; 停留时间 &gt; 下一屏 &gt; 其余（刚滚出的留在最后，交给保留期处理）。</item>
/// <item>去重：连续提交同一批候选不会反复触发（内容没变就不吐）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class MediaPrefetchPriorityGate : IDisposable
{
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private IReadOnlyList<MediaPrefetchCandidate> _last = [];
    private string _lastSignature = string.Empty;
    private long _version;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaPrefetchPriorityGate"/> class.
    /// </summary>
    /// <param name="debounce">安静多久算「滚动稳定」.</param>
    public MediaPrefetchPriorityGate(TimeSpan debounce)
    {
        _debounce = debounce <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(500) : debounce;
    }

    /// <summary>
    /// 滚动稳定、候选确定后触发（在后台线程；同一批内容连续触发只会来一次）.
    /// </summary>
    public event Action<IReadOnlyList<MediaPrefetchCandidate>>? Stabilized;

    /// <summary>
    /// 防抖时长.
    /// </summary>
    public TimeSpan Debounce => _debounce;

    /// <summary>
    /// 提交一次候选快照（每次滚动 / 尺寸变化都调；内部会重置计时器）.
    /// </summary>
    /// <param name="candidates">当前候选.</param>
    public void Submit(IReadOnlyList<MediaPrefetchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var ordered = Sort(candidates);
        var signature = BuildSignature(ordered);
        CancellationToken token;
        long version;
        lock (_gate)
        {
            version = ++_version;
            // 内容没变（例如只是计时器重入）就不重置：否则一直有 ViewChanged 会永远等不到安静期.
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastSignature = signature;
            _last = ordered;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }

        _ = DebounceAsync(version, ordered, token);
    }

    /// <summary>
    /// 取消挂起的防抖（页面关闭 / 切走时调）.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _version++;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
            _last = [];
            _lastSignature = string.Empty;
        }
    }

    /// <summary>
    /// 按优先级排序（纯函数，测试与诊断可直接用）.
    /// </summary>
    /// <param name="candidates">候选.</param>
    /// <returns>排好序的新列表.</returns>
    public static IReadOnlyList<MediaPrefetchCandidate> Sort(IReadOnlyList<MediaPrefetchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ordered = candidates.ToList();
        ordered.Sort(Compare);
        return ordered;
    }

    /// <summary>
    /// 优先级比较：悬停 / 焦点 / 正在播 &gt; 可见面积 &gt; 停留时间 &gt; 下一屏 &gt; 其它.
    /// </summary>
    /// <param name="left">左.</param>
    /// <param name="right">右.</param>
    /// <returns>比较结果.</returns>
    public static int Compare(MediaPrefetchCandidate? left, MediaPrefetchCandidate? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var tierCompare = Tier(left).CompareTo(Tier(right));
        if (tierCompare != 0)
        {
            return tierCompare;
        }

        var ratioCompare = right.VisibleRatio.CompareTo(left.VisibleRatio);
        if (Math.Abs(right.VisibleRatio - left.VisibleRatio) > 0.001)
        {
            return ratioCompare;
        }

        var dwellCompare = right.Dwell.CompareTo(left.Dwell);
        if (dwellCompare != 0)
        {
            return dwellCompare;
        }

        return left.Index.CompareTo(right.Index);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Cancel();
    }

    private static int Tier(MediaPrefetchCandidate candidate)
    {
        if (candidate.IsPlaying || candidate.IsHovered || candidate.IsFocused)
        {
            return 0;
        }

        if (candidate.VisibleRatio > 0.001)
        {
            return 1;
        }

        return candidate.IsNextScreen ? 2 : 3;
    }

    private static string BuildSignature(IReadOnlyList<MediaPrefetchCandidate> candidates)
    {
        var builder = new System.Text.StringBuilder(candidates.Count * 32);
        foreach (var candidate in candidates)
        {
            builder.Append(candidate.VideoId).Append(':')
                .Append(Math.Round(candidate.VisibleRatio, 2).ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(candidate.IsHovered ? '1' : '0')
                .Append(candidate.IsFocused ? '1' : '0')
                .Append(candidate.IsPlaying ? '1' : '0')
                .Append(candidate.IsNextScreen ? '1' : '0')
                .Append(';');
        }

        return builder.ToString();
    }

    private async Task DebounceAsync(long version, IReadOnlyList<MediaPrefetchCandidate> candidates, CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounce, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (version != _version)
            {
                return;
            }
        }

        try
        {
            Stabilized?.Invoke(candidates);
        }
        catch
        {
            // 订阅方的异常不应打断预取调度本身.
        }
    }
}
