// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 播放器交互性能探针（可开关）.
/// 开关：LocalState 下存在名为 <c>perf-probe.txt</c> 的文件即启用，删掉即关闭。
/// 启用后只写 Summary 级日志，不逐帧刷屏。
/// </summary>
public static class PerfProbe
{
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
    /// 每 10 秒把累计样本汇总成一行日志（P50/P95/Max）.
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
                pair.Value.Clear();
            }

            foreach (var pair in Counters)
            {
                Serilog.Log.Information("PERF COUNT {Name}={Value}", pair.Key, pair.Value);
            }

            Counters.Clear();
        }
    }
}
