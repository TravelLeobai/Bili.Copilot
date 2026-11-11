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

    /// <summary>
    /// 详情侧栏挂载耗时的探针名.
    /// 采样值是"连接器建完区块（InitializeSections 返回）"到"侧栏根元素真正 Loaded"之间的毫秒数。
    /// </summary>
    public const string DetailPanelMountProbeName = "Detail.Panel.Mount";

    /// <summary>
    /// 详情侧栏首次渲染的探针名.
    /// 采样值是从挂载处投一个低优先级回调开始，到这个回调真正跑起来之间的毫秒数，
    /// 近似"UI 线程忙完当前这轮布局/渲染、回到空闲点"的时间。
    /// </summary>
    public const string DetailPanelFirstRenderProbeName = "Detail.Panel.FirstRender";

    /// <summary>
    /// 封面解码耗时的探针名（毫秒）。
    /// </summary>
    public const string CoverDecodeProbeName = "Cover.Decode";

    /// <summary>
    /// 封面解码宽度的探针名（像素）。
    /// </summary>
    public const string CoverDecodeWidthProbeName = "Cover.DecodeWidth";

    private const int UiLagIntervalMs = 50;

    // 周期汇总的间隔：只求"不用交互也能看到数"，不做高频刷新.
    private const int FlushIntervalSeconds = 10;

    // 挂载计时的止损上限：超过这个值说明这次挂载不是"建完区块紧接着挂上"，样本丢掉.
    private const int MaxPanelMountSampleMs = 30000;

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
    private static long _panelMountStartTimestamp;
    private static int _panelMountArmed;
    private static DispatcherQueueTimer? _flushTimer;
    private static int _flushTimerStarted;

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
    /// 开始计"详情侧栏挂上"这一段：从连接器建完区块（<c>InitializeSections()</c> 返回）算起.
    /// 只在探针启用时生效；可重复调用，以最后一次为准（换视频会重新开始）.
    /// </summary>
    public static void BeginDetailPanelMountProbe()
    {
        if (!IsEnabled)
        {
            return;
        }

        Volatile.Write(ref _panelMountStartTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _panelMountArmed, 1);
    }

    /// <summary>
    /// 详情侧栏根元素已经挂到可视树上（Loaded）：记一笔 <see cref="DetailPanelMountProbeName"/>.
    /// 只在 <see cref="BeginDetailPanelMountProbe"/> 之后的第一次挂载记录，重复调用无副作用；
    /// 探针关闭时是空操作.
    /// </summary>
    public static void CompleteDetailPanelMountProbe()
    {
        if (!IsEnabled || Interlocked.Exchange(ref _panelMountArmed, 0) == 0)
        {
            return;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(Volatile.Read(ref _panelMountStartTimestamp)).TotalMilliseconds;
        if (elapsedMs > MaxPanelMountSampleMs)
        {
            // 隔了这么久说明中间面板根本没在等这次构建（例如信息区块不是当前选中项），
            // 这种区间不是我们要量的那段，丢掉不记.
            return;
        }

        Record(DetailPanelMountProbeName, elapsedMs);
    }

    /// <summary>
    /// 在详情侧栏挂载的位置投一次采样：低优先级回调排在当前这一轮布局/渲染之后，
    /// 它跑起来的时间就近似"挂载后 UI 线程回到下一个空闲点"，记进 <see cref="DetailPanelFirstRenderProbeName"/>.
    /// 没有正在计时的挂载窗口、或探针关闭时是空操作.
    /// </summary>
    public static void QueueDetailPanelFirstRenderProbe()
    {
        if (!IsEnabled || Volatile.Read(ref _panelMountArmed) == 0)
        {
            return;
        }

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        var posted = Stopwatch.GetTimestamp();
        DispatcherQueueHandler callback = () =>
            Record(DetailPanelFirstRenderProbeName, Stopwatch.GetElapsedTime(posted).TotalMilliseconds);

        // Low 优先级最接近"当前这批 UI 活干完之后的空闲点"；某些环境下 Low 不受支持，退回默认优先级.
        if (!queue.TryEnqueue(DispatcherQueuePriority.Low, callback))
        {
            _ = queue.TryEnqueue(callback);
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

        // 周期汇总跟着采样一起起来：不靠顶栏交互，探针启用期间 10 秒一次把累计样本写出去.
        StartFlushTimer(queue);

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
    /// 一并停掉周期汇总定时器，不留一个空转的 UI 定时器.
    /// </summary>
    public static void StopUiLagProbe()
    {
        StopFlushTimer();
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

    /// <summary>
    /// 起周期汇总定时器（10 秒一次，跑在 UI 队列上）.
    /// 只在探针启用时才存在：<see cref="StartUiLagProbe"/> 已经挡过一层，这里再挡一次，
    /// 保证关掉探针时连定时器都不建。可重复调用，只有首次生效.
    /// </summary>
    /// <param name="queue">UI 线程的调度队列.</param>
    private static void StartFlushTimer(DispatcherQueue queue)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _flushTimerStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(FlushIntervalSeconds);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                try
                {
                    FlushIfDue();
                }
                catch
                {
                    // 队列/日志在关闭途中：安静跳过这一拍，不往外抛.
                }
            };
            _flushTimer = timer;
            timer.Start();
        }
        catch
        {
            // 队列不可用（例如窗口已经销毁）：没有定时器也不影响其它采样.
            Interlocked.Exchange(ref _flushTimerStarted, 0);
        }
    }

    private static void StopFlushTimer()
    {
        var timer = Interlocked.Exchange(ref _flushTimer, null);
        Interlocked.Exchange(ref _flushTimerStarted, 0);
        if (timer is null)
        {
            return;
        }

        try
        {
            timer.Stop();
        }
        catch
        {
            // 队列已经在关闭：停不下来就算了，不要往外抛.
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
