// Copyright (c) Bili Copilot. All rights reserved.

#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using global::Microsoft.Win32.SafeHandles;

namespace BiliCopilot.UI.Services.Diagnostics;

/// <summary>
/// 一次 dump 写入的结果.
/// </summary>
/// <param name="FileName">文件名（不含目录）.</param>
/// <param name="WriteMilliseconds">写入耗时（毫秒）.</param>
/// <param name="Bytes">文件字节数.</param>
internal readonly record struct PlaybackStallDumpResult(string FileName, double WriteMilliseconds, long Bytes);

/// <summary>
/// 播放停滞 dump 的落盘与保留.
/// <para>
/// <b>隐私</b>：进程 dump 里含该进程的托管堆与线程栈，明文可能出现 Cookie、access_token、
/// 带鉴权参数的完整 URL。所以这里只写本地目录，绝不上传、不自动附 Issue、不写公开仓库；
/// 目录里同时放一个 <c>.gitignore</c>（内容 <c>*</c>），万一被指到仓库里也不会误提交.
/// </para>
/// </summary>
internal static class PlaybackStallDump
{
    /// <summary>
    /// MiniDumpNormal（0）| MiniDumpWithThreadInfo（0x1000）.
    /// <para>
    /// MiniDumpNormal 本身包含每个线程的栈内存，配合 <c>MiniDumpWithThreadInfo</c> 补上 TEB / 线程时间，
    /// 足以让 <c>dotnet-dump analyze</c> 走出全部线程的托管栈；刻意<b>不</b>加
    /// <c>MiniDumpWithFullMemory</c>：体积会到几百 MB，而且会把整份堆（含令牌）原样落盘.
    /// </para>
    /// </summary>
    public const uint DumpFlags = 0x00001000;

    /// <summary>
    /// 目录里最多保留几个 dump.
    /// </summary>
    public const int MaximumDumps = 10;

    /// <summary>
    /// 目录里 dump 的总字节上限（200MB）.
    /// </summary>
    public const long MaximumTotalBytes = 200L * 1024 * 1024;

    private const string DumpSearchPattern = "stall-*.dmp";

    // DbgHelp 全家（含 MiniDumpWriteDump）都是单线程实现，多线程并发调用会互相踩内存.
    private static readonly object DbgHelpSync = new();

    /// <summary>
    /// 往目录里写一个 dump.
    /// </summary>
    /// <param name="directory">目标目录（不存在会建）.</param>
    /// <param name="utcTimestamp">文件名里用的 UTC 时间.</param>
    /// <param name="context">写进同名 <c>.txt</c> 侧车的上下文（不含明文凭据）.</param>
    /// <param name="result">成功时的结果.</param>
    /// <param name="error">失败原因（错误码 / 异常类型名）.</param>
    /// <returns>写成功返回 <see langword="true"/>.</returns>
    public static bool TryWrite(
        string directory,
        DateTime utcTimestamp,
        string? context,
        out PlaybackStallDumpResult result,
        out string error)
    {
        result = default;
        error = string.Empty;
        string? path = null;
        var created = false;

        // 串行化：同一时刻只允许一个 DbgHelp 调用.
        lock (DbgHelpSync)
        {
            try
            {
                Directory.CreateDirectory(directory);
                EnsureGitIgnore(directory);

                var fileName = "stall-"
                    + utcTimestamp.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss.fffffff'Z'", CultureInfo.InvariantCulture)
                    + ".dmp";
                path = Path.Combine(directory, fileName);
                var startedAt = Stopwatch.GetTimestamp();
                long bytes;
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;

                    // hProcess 用 GetCurrentProcess() 的伪句柄，hProcessId 用真实进程号：
                    // hProcessId 只用于校验（内核会在参数不一致时拒绝），两者必须指向同一进程.
                    if (!MiniDumpWriteDump(
                            GetCurrentProcess(),
                            (uint)Environment.ProcessId,
                            file.SafeFileHandle,
                            DumpFlags,
                            0,
                            0,
                            0))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    bytes = file.Length;
                }

                var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                TryWriteContext(directory, fileName, context, bytes, elapsed);
                Prune(directory);
                result = new(fileName, elapsed, bytes);
                return true;
            }
            catch (Exception ex)
            {
                error = ex is Win32Exception native
                    ? "0x" + native.NativeErrorCode.ToString("X8", CultureInfo.InvariantCulture)
                    : ex.GetType().Name;

                // 失败必须清残留：半个 dump 没有分析价值，还会一直占着保留名额.
                if (created && path is not null)
                {
                    TryDelete(path);
                    TryDelete(Path.ChangeExtension(path, ".txt"));
                }

                return false;
            }
        }
    }

    /// <summary>
    /// 按数量 + 总大小清理目录（最旧的先删）.
    /// </summary>
    /// <param name="directory">目标目录.</param>
    public static void Prune(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.EnumerateFiles(directory, DumpSearchPattern)
            .Select(p => new FileInfo(p))
            .OrderBy(p => p.LastWriteTimeUtc)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            return;
        }

        var totalBytes = files.Sum(p => p.Length);
        var index = 0;

        // 数量上限.
        while (files.Length - index > MaximumDumps)
        {
            if (DeleteAt(files[index]))
            {
                totalBytes -= files[index].Length;
            }

            index++;
        }

        // 总大小上限.
        while (totalBytes > MaximumTotalBytes && index < files.Length)
        {
            if (DeleteAt(files[index]))
            {
                totalBytes -= files[index].Length;
            }

            index++;
        }
    }

    /// <summary>
    /// 清空 dump 目录（文档里的手动清理入口也走它）.
    /// </summary>
    /// <param name="directory">目标目录.</param>
    /// <returns>删掉的文件数.</returns>
    public static int Cleanup(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, DumpSearchPattern).ToArray())
        {
            if (TryDelete(file))
            {
                removed++;
            }

            TryDelete(Path.ChangeExtension(file, ".txt"));
        }

        return removed;
    }

    private static bool DeleteAt(FileInfo file)
    {
        if (!TryDelete(file.FullName))
        {
            return false;
        }

        TryDelete(Path.ChangeExtension(file.FullName, ".txt"));
        return true;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryWriteContext(string directory, string fileName, string? context, long bytes, double elapsed)
    {
        if (string.IsNullOrEmpty(context))
        {
            return;
        }

        try
        {
            var header = string.Create(
                CultureInfo.InvariantCulture,
                $"file={fileName}\nbytes={bytes}\nwrite-ms={elapsed:0.0}\nutc={DateTime.UtcNow:O}\n");
            File.WriteAllText(Path.ChangeExtension(Path.Combine(directory, fileName), ".txt"), header + context);
        }
        catch (IOException)
        {
            // 侧车写不进去不影响 dump 本身.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // 万一有人把 dump 目录指进仓库：这里放一个全忽略的 .gitignore，避免 dump 被提交.
    private static void EnsureGitIgnore(string directory)
    {
        try
        {
            var path = Path.Combine(directory, ".gitignore");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "*\n");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dbghelp.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        nint process,
        uint processId,
        SafeFileHandle file,
        uint dumpType,
        nint exceptionParam,
        nint userStreamParam,
        nint callbackParam);
}
#endif
