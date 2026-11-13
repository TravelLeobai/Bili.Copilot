// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 三级探针：把 <see cref="PerfProbe.LoadWidgetMountProbeName"/>（加载入口 → 播放器覆盖层第一次 Loaded）
/// 这一段拆开，用来判断「点视频后播放器白屏卡死」的那 20 多秒到底花在哪一级。
/// <para>1. <c>widget-ctor</c>：播放器覆盖层控件实例化（XAML InitializeComponent + 内部定时器构造）。</para>
/// <para>2. <c>layout</c>：控件挂进窗口可视树（SetUIElement）→ 第一次 Loaded，含中间的同步初始化与首轮布局。</para>
/// <para>3. <c>first-frame</c>：Loaded → 第一次 CompositionTarget.Rendering（首帧提交完成）。</para>
/// 只在 LocalState 下存在 perf-probe.txt 时生效；关掉时所有方法都直接返回，不改任何业务行为。
/// 输出统一前缀 <c>PERFWM3:</c>，每条都带 <c>cost=xxxms</c>；原有 Load.WidgetMount 保持不动，方便前后对比。
/// </summary>
public static class PerfWidgetMountTrace
{
    /// <summary>
    /// 日志前缀：抓日志用 "PERFWM3:" 一把捞全。
    /// </summary>
    public const string Prefix = "PERFWM3:";

    private static readonly object SyncRoot = new();
    private static readonly List<string> Timeline = [];
    private static long _entryTimestamp;
    private static long _ctorStartTimestamp;
    private static long _mountTimestamp;
    private static long _firstAwaitTimestamp;
    private static long _mpvAwaitStartTimestamp;
    private static long _mpvAwaitEndTimestamp;
    private static long _loadedTimestamp;
    private static int _tracing;
    private static int _firstFrameReported;
    private static int _fallbackTimerStarted;
    private static DispatcherQueueTimer? _fallbackTimer;

    /// <summary>
    /// 是否启用（跟随 <see cref="PerfProbe.IsEnabled"/> 的 perf-probe.txt 开关）。
    /// </summary>
    public static bool IsEnabled => PerfProbe.IsEnabled;

    /// <summary>
    /// 开始一轮三级计时：在播放器加载入口调用，记下总起点并清空上一轮的时间线。
    /// </summary>
    public static void Begin()
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (SyncRoot)
        {
            Timeline.Clear();
            _entryTimestamp = Stopwatch.GetTimestamp();
            _ctorStartTimestamp = 0L;
            _mountTimestamp = 0L;
            _firstAwaitTimestamp = 0L;
            _mpvAwaitStartTimestamp = 0L;
            _mpvAwaitEndTimestamp = 0L;
            _loadedTimestamp = 0L;
            Volatile.Write(ref _firstFrameReported, 0);
            Interlocked.Exchange(ref _fallbackTimerStarted, 0);
            Volatile.Write(ref _tracing, 1);
        }
    }

    /// <summary>
    /// 记一个检查点：只累计到 layout 那行的 detail 时间线里，不单独刷日志（零行为影响）。
    /// </summary>
    /// <param name="name">检查点名.</param>
    public static void Mark(string name)
    {
        if (Volatile.Read(ref _tracing) == 0)
        {
            return;
        }

        var elapsedMs = ElapsedMs(Volatile.Read(ref _entryTimestamp));
        lock (SyncRoot)
        {
            if (Volatile.Read(ref _tracing) == 0)
            {
                return;
            }

            Timeline.Add($"{name}={elapsedMs:F1}ms");
        }
    }

    /// <summary>
    /// 第一级起点：播放器覆盖层控件即将实例化。
    /// </summary>
    public static void MarkCtorStart()
    {
        if (Volatile.Read(ref _tracing) == 0)
        {
            return;
        }

        Volatile.Write(ref _ctorStartTimestamp, Stopwatch.GetTimestamp());
        Mark("overlay-ctor-start");
    }

    /// <summary>
    /// 第一级终点：播放器覆盖层控件实例化完成，记 <c>PERFWM3:widget-ctor</c>。
    /// </summary>
    /// <param name="detail">附加说明（不要放隐私内容）.</param>
    public static void MarkCtorEnd(string detail = "")
    {
        if (Volatile.Read(ref _tracing) == 0)
        {
            return;
        }

        Mark("overlay-ctor-end");
        var start = Volatile.Read(ref _ctorStartTimestamp);
        if (start == 0L)
        {
            return;
        }

        var costMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Write("widget-ctor", costMs, string.IsNullOrEmpty(detail) ? "control=PlayerOverlay" : detail);
        PerfProbe.Record("WM3.WidgetCtor", costMs);
    }

    /// <summary>
    /// 第二级起点：控件即将挂进播放窗口可视树。
    /// </summary>
    public static void MarkMount()
    {
        if (Volatile.Read(ref _tracing) == 0)
        {
            return;
        }

        Volatile.Write(ref _mountTimestamp, Stopwatch.GetTimestamp());
        Mark("set-ui-element");
    }

    /// <summary>
    /// 记录「第一个真的可能让出线程的 await」的位置：用来把 layout 段拆成
    /// 「纯同步 UI 活」和「await 之后才回来的布局/渲染」两半。
    /// </summary>
    public static void MarkFirstAwait()
    {
        if (Volatile.Read(ref _tracing) == 0)
        {
            return;
        }

        Volatile.Write(ref _firstAwaitTimestamp, Stopwatch.GetTimestamp());
        Mark("first-await");
    }

    /// <summary>
    /// 记录 mpv 实例初始化那次 await 的起点。
    /// </summary>
    public static void MarkMpvAwaitStart()
    {
        if (Volatile.Read(ref _tracing) == 0)
        {
            return;
        }

        Volatile.Write(ref _mpvAwaitStartTimestamp, Stopwatch.GetTimestamp());
        Mark("mpv-await-start");
    }

    /// <summary>
    /// 记录 mpv 实例初始化那次 await 的终点（正常/超时/取消都算）。
    /// </summary>
    public static void MarkMpvAwaitEnd()
    {
        if (Volatile.Read(ref _tracing) == 0)
        {
            return;
        }

        Volatile.Write(ref _mpvAwaitEndTimestamp, Stopwatch.GetTimestamp());
        Mark("mpv-await-end");
    }

    /// <summary>
    /// 第二级终点：播放器覆盖层第一次 Loaded。记 <c>PERFWM3:layout</c>，并起算首帧。
    /// 重复调用无副作用；探针关闭时是空操作.
    /// </summary>
    public static void CompleteLoaded()
    {
        if (Interlocked.Exchange(ref _tracing, 0) == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _loadedTimestamp, now);
        var mount = Volatile.Read(ref _mountTimestamp);
        var layoutMs = mount == 0L ? ElapsedMs(Volatile.Read(ref _entryTimestamp)) : Stopwatch.GetElapsedTime(mount).TotalMilliseconds;
        lock (SyncRoot)
        {
            Timeline.Add($"loaded={ElapsedMs(Volatile.Read(ref _entryTimestamp)):F1}ms");
        }

        Write("layout", layoutMs, BuildLayoutDetail());
        PerfProbe.Record("WM3.Layout", layoutMs);
        ArmFirstFrameProbe();
    }

    private static string BuildLayoutDetail()
    {
        var entry = Volatile.Read(ref _entryTimestamp);
        var mount = Volatile.Read(ref _mountTimestamp);
        var firstAwait = Volatile.Read(ref _firstAwaitTimestamp);
        var mpvStart = Volatile.Read(ref _mpvAwaitStartTimestamp);
        var mpvEnd = Volatile.Read(ref _mpvAwaitEndTimestamp);
        var loaded = Volatile.Read(ref _loadedTimestamp);

        var builder = new StringBuilder();
        builder.Append($"entryToMount={DeltaMs(entry, mount):F1}ms");
        builder.Append($" mountToFirstAwait={DeltaMs(mount, firstAwait):F1}ms");
        builder.Append($" firstAwaitToMpvAwait={DeltaMs(firstAwait, mpvStart):F1}ms");
        builder.Append($" mpvAwait={DeltaMs(mpvStart, mpvEnd):F1}ms");
        builder.Append($" mpvAwaitEndToLoaded={DeltaMs(mpvEnd, loaded):F1}ms");
        builder.Append($" total={DeltaMs(entry, loaded):F1}ms");
        builder.Append(" marks=[");
        lock (SyncRoot)
        {
            builder.AppendJoin(",", Timeline);
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static void ArmFirstFrameProbe()
    {
        try
        {
            CompositionTarget.Rendering += OnFirstRendering;
        }
        catch
        {
            // 取不到合成器事件就只靠下面的兜底.
        }

        // 兜底：窗口没合成 / Rendering 迟迟不来时，用一个一次性定时器在 UI 空闲点记一笔，
        // detail 里会标明来源，别把兜底样本当成真首帧.
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null || Interlocked.CompareExchange(ref _fallbackTimerStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1200);
            timer.IsRepeating = false;
            timer.Tick += OnFallbackTimerTick;
            _fallbackTimer = timer;
            timer.Start();
        }
        catch
        {
            // 队列不可用时什么都不做.
        }
    }

    private static void OnFallbackTimerTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            sender.Tick -= OnFallbackTimerTick;
            sender.Stop();
        }
        catch
        {
        }

        ReportFirstFrame("source=DispatcherQueueTimer(fallback)");
    }

    private static void OnFirstRendering(object? sender, object e)
        => ReportFirstFrame("source=CompositionTarget.Rendering");

    private static void ReportFirstFrame(string source)
    {
        if (Interlocked.Exchange(ref _firstFrameReported, 1) != 0)
        {
            return;
        }

        try
        {
            CompositionTarget.Rendering -= OnFirstRendering;
        }
        catch
        {
        }

        var timer = Interlocked.Exchange(ref _fallbackTimer, null);
        if (timer is not null)
        {
            try
            {
                timer.Stop();
            }
            catch
            {
            }
        }

        var loaded = Volatile.Read(ref _loadedTimestamp);
        if (loaded == 0L)
        {
            return;
        }

        var costMs = Stopwatch.GetElapsedTime(loaded).TotalMilliseconds;
        Write("first-frame", costMs, $"{source} loadedToFirstFrame={costMs:F1}ms total={ElapsedMs(Volatile.Read(ref _entryTimestamp)):F1}ms");
        PerfProbe.Record("WM3.FirstFrame", costMs);
    }

    private static void Write(string level, double costMs, string detail)
        => Serilog.Log.Information("{Prefix}{Level} cost={Cost:F2}ms detail={Detail}", Prefix, level, costMs, detail);

    private static double ElapsedMs(long startTimestamp)
        => startTimestamp == 0L ? 0d : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private static double DeltaMs(long from, long to)
        => from == 0L || to == 0L ? 0d : Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}
