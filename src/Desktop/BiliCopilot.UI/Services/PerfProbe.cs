// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 播放器交互性能探针（可开关）.
/// 开关：LocalState 下存在名为 <c>perf-probe.txt</c> 的文件即启用，删掉即关闭。
/// 启用后只写 Summary 级日志，不逐帧刷屏。
/// </summary>
public static class PerfProbe
{
    /// <summary>
    /// UI 线程排队延迟的探针名.
    /// 采样值是"投递到 UI 队列"到"回调真正执行"之间的毫秒数，不逐条写日志。
    /// </summary>
    public const string UiLagProbeName = "UiLag";

    private const int UiLagIntervalMs = 50;

    private static readonly Lazy<bool> EnabledLazy = new(() =>
    {
        try
        {
            var path = System.IO.Path.Combine(
                Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path,
                "perf-probe.txt");
            return System.IO.File.Exists(path);
        }
        catch
        {
            return false;
        }
    });

    private static readonly Dictionary<string, List<double>> Samples = [];
    private static readonly Dictionary<string, int> Counters = [];
    private static readonly object SyncRoot = new();
    private static DateTimeOffset _lastFlush = DateTimeOffset.Now;
    private static CancellationTokenSource? _uiLagCts;
    private static int _uiLagStarted;

    /// <summary>
    /// 是否启用.
    /// </summary>
    public static bool IsEnabled => EnabledLazy.Value;

    /// <summary>
    /// 记一次耗时（毫秒）.
    /// </summary>
    /// <param name="name">探针名.</param>
    /// <param name="elapsedMs">耗时.</param>
    public static void Record(string name, double elapsedMs)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (!Samples.TryGetValue(name, out var list))
            {
                list = [];
                Samples[name] = list;
            }

            list.Add(elapsedMs);
        }
    }

    /// <summary>
    /// 计数 +1（看调用频率，例如每秒写了多少次 IsControlsVisible）.
    /// </summary>
    /// <param name="name">计数器名.</param>
    public static void Count(string name)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (SyncRoot)
        {
            Counters[name] = Counters.TryGetValue(name, out var value) ? value + 1 : 1;
        }
    }

    /// <summary>
    /// 计时一段同步代码.
    /// </summary>
    /// <param name="name">探针名.</param>
    /// <param name="action">要执行的代码.</param>
    public static void Measure(string name, Action action)
    {
        if (!IsEnabled)
        {
            action();
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            action();
        }
        finally
        {
            Record(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    /// <summary>
    /// 启动 UI 线程排队延迟采样.
    /// 后台每 50ms 记一个时间戳并把空回调投进 UI 队列；回调真正跑起来时用"现在 - 投递时刻"
    /// 算排队延迟，记进 <see cref="UiLagProbeName"/>，不逐条写日志。
    /// 可重复调用，只有首次生效；探针关闭时零开销直接返回。
    /// 队列已经在关闭（TryEnqueue 失败或抛异常）时安静跳过，采样线程不受影响。
    /// </summary>
    /// <param name="queue">UI 线程的调度队列.</param>
    public static void StartUiLagProbe(DispatcherQueue queue)
    {
        if (queue is null || !IsEnabled)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _uiLagStarted, 1, 0) != 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _uiLagCts = cts;
        _ = Task.Run(() => UiLagLoopAsync(queue, cts.Token));
    }

    /// <summary>
    /// 停止 UI 线程排队延迟采样（重复调用无副作用，停掉之后可以再 Start）.
    /// </summary>
    public static void StopUiLagProbe()
    {
        var cts = Interlocked.Exchange(ref _uiLagCts, null);
        Interlocked.Exchange(ref _uiLagStarted, 0);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
            // 取消本身失败也要让采样线程自己退出，这里不往外抛.
        }
    }

    /// <summary>
    /// 每 10 秒把累计样本汇总成一行日志（P50/P95/Max）.
    /// UiLag 额外按 16.7 / 33.3 / 50 / 100 / 200ms 分段打一行桶统计（帧预算口径）.
    /// </summary>
    public static void FlushIfDue()
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (DateTimeOffset.Now - _lastFlush < TimeSpan.FromSeconds(10))
            {
                return;
            }

            _lastFlush = DateTimeOffset.Now;
            foreach (var pair in Samples)
            {
                if (pair.Value.Count == 0)
                {
                    continue;
                }

                var sorted = pair.Value.OrderBy(p => p).ToList();
                var p50 = sorted[(int)(sorted.Count * 0.5)];
                var p95 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];
                Serilog.Log.Information(
                    "PERF {Name}: n={Count} p50={P50:F2}ms p95={P95:F2}ms max={Max:F2}ms",
                    pair.Key,
                    sorted.Count,
                    p50,
                    p95,
                    sorted[^1]);
                if (string.Equals(pair.Key, UiLagProbeName, StringComparison.Ordinal))
                {
                    LogUiLagBuckets(sorted, p50, p95);
                }

                pair.Value.Clear();
            }

            foreach (var pair in Counters)
            {
                Serilog.Log.Information("PERF COUNT {Name}={Value}", pair.Key, pair.Value);
            }

            Counters.Clear();
        }
    }

    private static async Task UiLagLoopAsync(DispatcherQueue queue, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UiLagIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (!PostUiLagSample(queue))
                {
                    // 队列已经进入关闭流程，采样到此为止.
                    break;
                }
            }
            catch
            {
                // UI 队列已经在关闭（例如窗口销毁）：安静退出采样，不往外抛.
                break;
            }
        }
    }

    private static bool PostUiLagSample(DispatcherQueue queue)
    {
        var posted = Stopwatch.GetTimestamp();
        return queue.TryEnqueue(() =>
            Record(UiLagProbeName, Stopwatch.GetElapsedTime(posted).TotalMilliseconds));
    }

    private static void LogUiLagBuckets(List<double> sorted, double p50, double p95)
    {
        var under17 = 0;
        var under33 = 0;
        var under50 = 0;
        var under100 = 0;
        var under200 = 0;
        var above200 = 0;
        foreach (var sample in sorted)
        {
            if (sample < 16.7)
            {
                under17++;
            }
            else if (sample < 33.3)
            {
                under33++;
            }
            else if (sample < 50)
            {
                under50++;
            }
            else if (sample < 100)
            {
                under100++;
            }
            else if (sample < 200)
            {
                under200++;
            }
            else
            {
                above200++;
            }
        }

        Serilog.Log.Information(
            "PERF UiLag buckets: <16.7={Under17} <33.3={Under33} <50={Under50} <100={Under100} <200={Under200} >=200={Above200} p50={P50:F2}ms p95={P95:F2}ms max={Max:F2}ms",
            under17,
            under33,
            under50,
            under100,
            under200,
            above200,
            p50,
            p95,
            sorted[^1]);
    }
}
