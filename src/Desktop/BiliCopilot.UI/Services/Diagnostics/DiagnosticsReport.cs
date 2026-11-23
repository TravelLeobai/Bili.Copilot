// Copyright (c) Bili Copilot. All rights reserved.

using System.IO.Compression;
using System.Reflection;
using Windows.System;

namespace BiliCopilot.UI.Services.Diagnostics;

/// <summary>
/// 播放器「属性」面板背后的数据：停滞 dump 清单、日志清单、一键导出.
/// 这里只做读取与打包，不产生 dump、不改日志配置.
/// </summary>
internal static class DiagnosticsReport
{
    /// <summary>停滞 dump 的目录（与 <see cref="PlaybackStallDump"/> 写入位置一致）.</summary>
    public static string DumpFolder => Path.Combine(Root, "Diagnostics", "StallDumps");

    /// <summary>日志目录.</summary>
    public static string LoggerFolder => Path.Combine(Root, "Logger");

    /// <summary>应用本地数据目录.</summary>
    public static string Root => Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path;

    /// <summary>导出包里放几天的日志.</summary>
    public static TimeSpan LogWindow { get; } = TimeSpan.FromDays(3);

    /// <summary>当前应用版本（取自包清单，取不到时退回程序集版本）.</summary>
    public static string AppVersion
    {
        get
        {
            try
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch (Exception)
            {
                return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "未知";
            }
        }
    }

    /// <summary>当前运行时版本.</summary>
    public static string RuntimeVersion => Environment.Version.ToString();

    /// <summary>列出停滞 dump，按时间从新到旧.</summary>
    /// <returns>dump 文件信息；目录不存在时返回空集合.</returns>
    public static IReadOnlyList<FileInfo> GetDumps()
    {
        try
        {
            if (!Directory.Exists(DumpFolder))
            {
                return [];
            }

            return new DirectoryInfo(DumpFolder)
                .GetFiles("stall-*.dmp", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>统计日志文件（受 <see cref="LogWindow"/> 约束）.</summary>
    /// <returns>窗口内的日志文件.</returns>
    public static IReadOnlyList<FileInfo> GetRecentLogs()
    {
        try
        {
            if (!Directory.Exists(LoggerFolder))
            {
                return [];
            }

            var since = DateTime.UtcNow - LogWindow;
            return new DirectoryInfo(LoggerFolder)
                .GetFiles("*.txt", SearchOption.TopDirectoryOnly)
                .Where(file => file.LastWriteTimeUtc >= since)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 把日志与 dump 打成一个 zip 放到桌面.
    /// 只读本地文件，不上传任何东西.
    /// 正在被写入的日志可能拿不到读锁：单文件失败只跳过，不让整个包失败.
    /// </summary>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>zip 的完整路径，以及包里的日志数、dump 数、字节数、被跳过的文件数.</returns>
    public static async Task<(string Path, int LogCount, int DumpCount, long Bytes, int SkippedCount)> ExportAsync(CancellationToken cancellationToken)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            desktop = Root;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(desktop, $"BiliCopilot-diag-{stamp}.zip");
        var logs = GetRecentLogs();
        var dumps = GetDumps();
        var skipped = 0;

        await Task.Run(
            () =>
            {
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var log in logs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        skipped += AddFile(archive, log.FullName, $"logs/{log.Name}");
                    }

                    foreach (var dump in dumps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        skipped += AddFile(archive, dump.FullName, $"dumps/{dump.Name}");

                        // dump 旁边那份同名 .txt 是采集时写的上下文，一并带上才有用.
                        var sidecar = Path.ChangeExtension(dump.FullName, ".txt");
                        if (File.Exists(sidecar))
                        {
                            skipped += AddFile(archive, sidecar, $"dumps/{Path.GetFileName(sidecar)}");
                        }
                    }

                    var manifest = archive.CreateEntry("manifest.md", CompressionLevel.Optimal);
                    using var writer = new StreamWriter(manifest.Open());
                    writer.Write(BuildManifest(logs, dumps, skipped));
                }

                File.WriteAllBytes(zipPath, buffer.ToArray());
            },
            cancellationToken).ConfigureAwait(false);

        var bytes = new FileInfo(zipPath).Length;
        Serilog.Log.Information(
            "诊断包已导出：logs={LogCount} dumps={DumpCount} bytes={Bytes} skipped={Skipped}",
            logs.Count,
            dumps.Count,
            bytes,
            skipped);
        return (zipPath, logs.Count, dumps.Count, bytes, skipped);
    }

    // 加一个文件进包.
    // 只用 CreateEntryFromFile 会打不开正在被写入的当天日志（它按 FileShare.Read 打开源文件），
    // 而当天日志恰恰是最有用的那份，所以这里自己开 FileStream 并显式允许读写共享。
    private static int AddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(sourcePath);
            using var target = entry.Open();
            source.CopyTo(target);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug("诊断包跳过 {Name}：{Type}", Path.GetFileName(sourcePath), ex.GetType().Name);
            return 1;
        }
    }

    /// <summary>在资源管理器里定位一个文件或目录.</summary>
    /// <param name="path">文件或目录的完整路径.</param>
    /// <returns>任务.</returns>
    public static async Task RevealAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path).AsTask().ConfigureAwait(false);
            _ = await Launcher.LaunchFolderAsync(folder).AsTask().ConfigureAwait(false);
            return;
        }

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
        _ = await Launcher.LaunchFileAsync(file).AsTask().ConfigureAwait(false);
    }

    private static string BuildManifest(IReadOnlyList<FileInfo> logs, IReadOnlyList<FileInfo> dumps, int skipped)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# BiliCopilot 诊断包");
        builder.AppendLine();
        builder.AppendLine($"- 导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 应用版本：{AppVersion}");
        builder.AppendLine($"- 运行时：{RuntimeVersion}");
        builder.AppendLine($"- 进程 Id：{Environment.ProcessId}");
        builder.AppendLine($"- 架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"- 系统：{Environment.OSVersion.VersionString}");
        builder.AppendLine();
        builder.AppendLine($"## 日志（近 {LogWindow.TotalDays:0} 天，{logs.Count} 个）");
        foreach (var log in logs)
        {
            builder.AppendLine($"- {log.Name} · {log.Length / 1024d:0.0} KB · {log.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        }

        builder.AppendLine();
        builder.AppendLine($"## 播放停滞 dump（{dumps.Count} 个）");
        if (dumps.Count == 0)
        {
            builder.AppendLine("- 无。Release 构建不采集；Debug 下只有判据命中时才会写。");
        }

        foreach (var dump in dumps)
        {
            builder.AppendLine($"- {dump.Name} · {dump.Length / 1024d / 1024d:0.0} MB · {dump.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        }

        builder.AppendLine();
        builder.AppendLine("> dump 是进程内存快照，可能含 Cookie、令牌、媒体地址等敏感内容。别上传、别附到公开 issue。");
        if (skipped > 0)
        {
            builder.AppendLine($"> 打包时有 {skipped} 个文件因被占用跳过（多半是正在写入的日志）。");
        }

        return builder.ToString();
    }
}
