// Copyright (c) Bili Copilot. All rights reserved.

using System.Reflection;
using System.Text;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 启动检查点日志.
/// 故意不放在包数据目录里：settings.dat / LocalState 一旦损坏，
/// 最需要看日志的时候反而写不进去。这里写到 %LOCALAPPDATA%\BiliCopilot.Startup\，
/// 与应用设置、状态容器无关。日志不含 Cookie / Token / 用户内容.
/// </summary>
public static class StartupTrace
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;

    private static string? _root;

    /// <summary>
    /// 启动痕迹根目录：优先包内 LocalFolder（一定可写），退化到用户目录、临时目录.
    /// Smoke Test 会在这三个位置都找 startup-ok.txt.
    /// </summary>
    public static string Root => _root ??= ResolveRoot();

    /// <summary>
    /// 候选目录（Smoke Test 需要知道全部）.
    /// </summary>
    public static IEnumerable<string> CandidateRoots()
    {
        // 构建输出目录：dev 注册时应用就从这里跑，一定可写，也不受包数据/设置状态影响.
        yield return Path.Combine(AppContext.BaseDirectory, "startup-trace");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliCopilot.Startup");

        var localFolder = string.Empty;
        try
        {
            localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            localFolder = string.Empty;
        }

        if (!string.IsNullOrEmpty(localFolder))
        {
            yield return Path.Combine(localFolder, "StartupTrace");
        }

        yield return Path.Combine(Path.GetTempPath(), "BiliCopilot.Startup");
    }

    /// <summary>
    /// 当日日志文件.
    /// </summary>
    public static string LogFile => Path.Combine(Root, $"startup-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Smoke Test 轮询用的"启动成功"标记.
    /// </summary>
    public static string OkMarker => Path.Combine(Root, "startup-ok.txt");

    private static string ResolveRoot()
    {
        foreach (var dir in CandidateRoots())
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return dir;
            }
            catch
            {
                // 换下一个候选.
            }
        }

        return Path.Combine(Path.GetTempPath(), "BiliCopilot.Startup");
    }

    /// <summary>
    /// 写一条检查点.
    /// </summary>
    /// <param name="stage">阶段名.</param>
    /// <param name="detail">附加信息（不要放隐私内容）.</param>
    public static void Mark(string stage, string detail = "")
    {
        try
        {
            EnsureInitialized();
            var line = $"{DateTime.Now:O} | pid={Environment.ProcessId} | {stage} | {detail}";
            lock (SyncRoot)
            {
                File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响启动.
        }
    }

    /// <summary>
    /// 写一条带异常的检查点（含 HRESULT 与 InnerException 链）.
    /// </summary>
    /// <param name="stage">阶段名.</param>
    /// <param name="ex">异常.</param>
    public static void Fail(string stage, Exception? ex)
    {
        if (ex is null)
        {
            Mark(stage, "exception=null");
            return;
        }

        var builder = new StringBuilder();
        builder.Append($"exception={ex.GetType().FullName}");
        builder.Append($" | message={Sanitize(ex.Message)}");
        builder.Append($" | hresult=0x{ex.HResult:X8}");
        var inner = ex.InnerException;
        var depth = 0;
        while (inner is not null && depth < 5)
        {
            builder.Append($" | inner{depth}={inner.GetType().Name}:{Sanitize(inner.Message)}(0x{inner.HResult:X8})");
            inner = inner.InnerException;
            depth++;
        }

        Mark(stage, builder.ToString());
        try
        {
            lock (SyncRoot)
            {
                File.AppendAllText(LogFile, ex + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 标记"启动成功"：窗口已经可交互。Smoke Test 靠这个文件判断，不看进程存活.
    /// </summary>
    public static void MarkReady()
    {
        try
        {
            EnsureInitialized();
            var text = $"pid={Environment.ProcessId}{Environment.NewLine}time={DateTime.Now:O}{Environment.NewLine}version={Version}{Environment.NewLine}sha={GitSha}{Environment.NewLine}";
            File.WriteAllText(OkMarker, text, Encoding.UTF8);
            Mark("STARTUP-OK", $"version={Version} sha={GitSha}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 应用版本.
    /// </summary>
    public static string Version
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// 编译时写入的 Git SHA（没写就是 unknown）.
    /// </summary>
    public static string GitSha
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("BILICOPILOT_GIT_SHA");
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(Root);
        // 每次启动先删掉各个候选目录里上次的"成功"标记：Smoke Test 只认这一次的.
        foreach (var dir in CandidateRoots())
        {
            try
            {
                var marker = Path.Combine(dir, "startup-ok.txt");
                if (File.Exists(marker))
                {
                    File.Delete(marker);
                }
            }
            catch
            {
            }
        }

        _initialized = true;
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var value = text.Replace("\r", " ").Replace("\n", " ");
        return value.Length <= 300 ? value : value[..300];
    }
}




