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

    /// <summary>
    /// 视频加载总耗时的探针名（毫秒）。
    /// 采样值是 <c>PlayerViewModel.InitializeAsync</c> 从进入到完成（含超时/取消返回）的整段。
    /// 一次加载只记一个样本：后一次加载开始会让前一次尚未收尾的样本作废。
    /// </summary>
    public const string LoadTotalProbeName = "Load.Total";

    /// <summary>
    /// 视频加载同步段耗时的探针名（毫秒）。
    /// 采样值从加载入口算到「第一个真的可能让出线程的 await 之前」，覆盖窗口显示、
    /// 播放器覆盖层构建、绑定属性重置这一整段同步 UI 活；不含任何网络或播放器初始化等待。
    /// </summary>
    public const string LoadSnapshotProbeName = "Load.Snapshot";

    /// <summary>
    /// mpv 播放器初始化等待耗时的探针名（毫秒）。
    /// 采样值只包住 <c>PlayerSurface.InitializeAsync()</c> 那一次 await（带 20 秒上限的那次），
    /// 超时/取消同样记一笔，值就是这次等待实际耗掉的时间。
    /// </summary>
    public const string LoadMpvInitProbeName = "Load.MpvInit";

    /// <summary>
    /// 弹幕初始化耗时的探针名（毫秒）。
    /// 采样值从弹幕初始化入口算到 <c>Danmaku.Initialize(...)</c> 返回，包含分段弹幕的拉取时间。
    /// 这段由 <c>Player.IsPlaybackInitialized</c> 触发，所以起点晚于 <see cref="LoadTotalProbeName"/> 的起点。
    /// </summary>
    public const string LoadDanmakuProbeName = "Load.Danmaku";

    /// <summary>
    /// 播放器控件首次挂载耗时的探针名（毫秒）。
    /// 采样值从加载入口算到播放器覆盖层（<c>PlayerOverlay</c>）第一次 Loaded。
    /// 只有真正创建覆盖层的那次加载才有样本：同一个播放窗口里换视频不会重建覆盖层，也就不会有新样本。
    /// </summary>
    public const string LoadWidgetMountProbeName = "Load.WidgetMount";

    /// <summary>
    /// mpv 初始化超时的计数器名。
    /// 命中「mpv 播放器初始化超过 20 秒没返回」那条分支时 +1，用来不 grep 日志也能数超时次数。
    /// </summary>
    public const string LoadMpvInitTimeoutCounterName = "Load.MpvInit.Timeout";

    private const int UiLagIntervalMs = 50;

    // 周期汇总的间隔：只求"不用交互也能看到数"，不做高频刷新.
    private const int FlushIntervalSeconds = 10;

    // 挂载计时的止损上限：超过这个值说明这次挂载不是"建完区块紧接着挂上"，样本丢掉.
    private const int MaxPanelMountSampleMs = 30000;

    // 播放器覆盖层挂载计时的止损上限：超过这个值说明这次 Loaded 不是本次加载引起的（控件早就挂上了），样本丢掉.
    private const int MaxWidgetMountSampleMs = 30000;

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
    private static int _loadGeneration;
    private static long _loadStartTimestamp;
    private static int _loadSnapshotRecorded;
    private static long _widgetMountStartTimestamp;
    private static int _widgetMountArmed;
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
    /// 开始一次视频加载计时：记下起点、作废上一轮尚未收尾的样本，并同时起算播放器覆盖层挂载。
    /// 每次 <c>PlayerViewModel.InitializeAsync</c> 进入时调用一次；探针关闭时返回 0，之后所有收尾调用都是空操作.
    /// </summary>
    /// <returns>本次加载的代号，收尾时原样传回.</returns>
    public static int BeginLoadProbe()
    {
        if (!IsEnabled)
        {
            return 0;
        }

        var token = Interlocked.Increment(ref _loadGeneration);
        var start = Stopwatch.GetTimestamp();
        Volatile.Write(ref _loadStartTimestamp, start);
        Volatile.Write(ref _widgetMountStartTimestamp, start);
        Interlocked.Exchange(ref _loadSnapshotRecorded, 0);
        Interlocked.Exchange(ref _widgetMountArmed, 1);
        return token;
    }

    /// <summary>
    /// 记同步段 <see cref="LoadSnapshotProbeName"/>。
    /// 调用点要放在加载链路里第一个「真的可能让出线程」的 await 之前；一次加载只记一个样本，
    /// 代号已经过期（又开始了新一轮加载）时丢弃.
    /// </summary>
    /// <param name="token"><see cref="BeginLoadProbe"/> 返回的加载代号.</param>
    public static void RecordLoadSnapshot(int token)
    {
        if (!IsEnabled || token == 0 || token != Volatile.Read(ref _loadGeneration))
        {
            return;
        }

        if (Interlocked.Exchange(ref _loadSnapshotRecorded, 1) != 0)
        {
            return;
        }

        Record(LoadSnapshotProbeName, Stopwatch.GetElapsedTime(Volatile.Read(ref _loadStartTimestamp)).TotalMilliseconds);
    }

    /// <summary>
    /// 记加载总耗时 <see cref="LoadTotalProbeName"/>：<c>InitializeAsync</c> 收尾时调用一次。
    /// 代号过期（这轮加载已被下一轮取代）时不记，保证一次加载只有一个样本.
    /// </summary>
    /// <param name="token"><see cref="BeginLoadProbe"/> 返回的加载代号.</param>
    public static void CompleteLoadProbe(int token)
    {
        if (!IsEnabled || token == 0 || token != Volatile.Read(ref _loadGeneration))
        {
            return;
        }

        Record(LoadTotalProbeName, Stopwatch.GetElapsedTime(Volatile.Read(ref _loadStartTimestamp)).TotalMilliseconds);
    }

    /// <summary>
    /// 播放器覆盖层已经挂到可视树上（Loaded）：记一笔 <see cref="LoadWidgetMountProbeName"/>。
    /// 只在 <see cref="BeginLoadProbe"/> 之后的第一次挂载记录，重复调用无副作用；探针关闭时是空操作.
    /// </summary>
    public static void CompleteWidgetMountProbe()
    {
        if (!IsEnabled || Interlocked.Exchange(ref _widgetMountArmed, 0) == 0)
        {
            return;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(Volatile.Read(ref _widgetMountStartTimestamp)).TotalMilliseconds;
        if (elapsedMs > MaxWidgetMountSampleMs)
        {
            // 隔了这么久说明这次 Loaded 不是本次加载引起的（覆盖层早就挂上了）,
            // 这种区间不是我们要量的那段，丢掉不记.
            return;
        }

        Record(LoadWidgetMountProbeName, elapsedMs);
    }

    /// <summary>
    /// 记一段独立耗时的起点（毫秒口径）。
    /// 用于包住单次 await 这类局部区间，例如 mpv 初始化：探针关闭时返回 0，配合
    /// <see cref="EndLoadSegment"/> 就是空操作，不分配也不计时.
    /// </summary>
    /// <returns>起点时间戳；探针关闭时为 0.</returns>
    public static long BeginLoadSegment()
        => IsEnabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>
    /// 记一段独立耗时的终点：起点为 0（探针关闭或没有起点）时直接返回.
    /// </summary>
    /// <param name="name">探针名.</param>
    /// <param name="startTimestamp"><see cref="BeginLoadSegment"/> 返回的起点时间戳.</param>
    public static void EndLoadSegment(string name, long startTimestamp)
    {
        if (startTimestamp == 0L)
        {
            return;
        }

        Record(name, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
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
