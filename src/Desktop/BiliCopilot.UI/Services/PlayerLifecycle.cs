// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Text;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 播放器生命周期追踪.
/// 只记事实：谁创建、谁释放、在哪个线程、谁调用的。不记 Cookie / Token / 账号信息.
/// 目的：把「Reactor 重新渲染」「XAML 控件重造」「mpv 实例重建」这三件事分开看清。
/// </summary>
internal static class PlayerLifecycle
{
    private static readonly object SyncRoot = new();
    private static int _sequence;

    /// <summary>
    /// 日志文件位置：跟随应用日志目录，没有就落到临时目录.
    /// </summary>
    private static string LogFile
    {
        get
        {
            var folder = App.LoggerFolder;
            if (string.IsNullOrEmpty(folder))
            {
                folder = Path.Combine(Path.GetTempPath(), "BiliCopilot");
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch
            {
            }

            return Path.Combine(folder, $"player-lifecycle-{DateTime.Now:yyyyMMdd}.log");
        }
    }

    /// <summary>
    /// 生成一个实例编号（进程内单调递增）.
    /// </summary>
    /// <returns>编号.</returns>
    public static int NextId() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// 记一条生命周期事件.
    /// </summary>
    /// <param name="kind">事件种类：create / attach / detach / dispose / reuse / render.</param>
    /// <param name="owner">所属对象（带实例号）.</param>
    /// <param name="detail">附加信息.</param>
    /// <param name="withStack">是否附调用栈（默认前 6 帧）.</param>
    public static void Log(string kind, string owner, string detail = "", bool withStack = false)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
            builder.Append(" | ").Append(kind.PadRight(8));
            builder.Append(" | ").Append(owner);
            builder.Append(" | tid=").Append(Environment.CurrentManagedThreadId);
            if (!string.IsNullOrEmpty(detail))
            {
                builder.Append(" | ").Append(detail);
            }

            if (withStack)
            {
                var frames = new StackTrace(1, false).GetFrames();
                var take = Math.Min(frames.Length, 6);
                for (var i = 0; i < take; i++)
                {
                    var method = frames[i].GetMethod();
                    if (method is null)
                    {
                        continue;
                    }

                    builder.Append(Environment.NewLine).Append("        ← ")
                        .Append(method.DeclaringType?.Name)
                        .Append('.')
                        .Append(method.Name);
                }
            }

            lock (SyncRoot)
            {
                File.AppendAllText(LogFile, builder.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 追踪本身失败不影响播放.
        }
    }

    /// <summary>
    /// 记录一次耗时事件（begin 返回的 Stopwatch 交给 End 使用）.
    /// </summary>
    /// <param name="owner">所属对象.</param>
    /// <param name="action">动作名.</param>
    /// <returns>计时器.</returns>
    public static Stopwatch Begin(string owner, string action)
    {
        Log($"{action}:start", owner);
        return Stopwatch.StartNew();
    }

    /// <summary>
    /// 结束计时并记录耗时.
    /// </summary>
    /// <param name="owner">所属对象.</param>
    /// <param name="action">动作名.</param>
    /// <param name="watch">计时器.</param>
    /// <param name="extra">附加信息.</param>
    public static void End(string owner, string action, Stopwatch watch, string extra = "")
    {
        watch.Stop();
        Log($"{action}:end", owner, $"elapsed={watch.ElapsedMilliseconds}ms {(string.IsNullOrEmpty(extra) ? string.Empty : extra)}");
    }
}
