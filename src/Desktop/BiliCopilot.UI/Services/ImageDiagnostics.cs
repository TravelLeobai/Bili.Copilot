// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 图片诊断：统计下载字节数、解码尺寸、加载耗时和失败次数.
/// 默认不启用——LocalState 下放一个 <c>image-diag.txt</c> 才开，避免平时拖慢主流程.
/// </summary>
internal static class ImageDiagnostics
{
    private const string FlagFileName = "image-diag.txt";

    private static readonly object FileLock = new();

    private static string? _csvPath;

    private static long _downloadCount;

    private static long _downloadBytes;

    private static long _loadedCount;

    private static long _failedCount;

    private static long _failedLogged;

    /// <summary>
    /// 是否启用诊断.
    /// </summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>
    /// 读取开关并准备 CSV 文件.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            var folder = Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path;
            IsEnabled = File.Exists(Path.Combine(folder, FlagFileName));
            if (!IsEnabled)
            {
                return;
            }

            _csvPath = Path.Combine(folder, $"image-diag-{DateTime.Now:yyyyMMdd}.csv");
            if (!File.Exists(_csvPath))
            {
                File.AppendAllText(
                    _csvPath,
                    "timestamp,kind,url,bytes,decodeWidth,decodeHeight,elapsedMs,message" + Environment.NewLine);
            }

            Serilog.Log.Information("图片诊断已启用：{Path}", _csvPath);
        }
        catch (Exception ex)
        {
            IsEnabled = false;
            Serilog.Log.Warning(ex, "图片诊断初始化失败");
        }
    }

    /// <summary>
    /// 记一次图片下载.
    /// </summary>
    /// <param name="url">地址.</param>
    /// <param name="bytes">字节数，未知填 -1.</param>
    /// <param name="elapsedMs">耗时（毫秒）.</param>
    public static void RecordDownload(string? url, long bytes, double elapsedMs)
    {
        if (!IsEnabled)
        {
            return;
        }

        _ = Interlocked.Increment(ref _downloadCount);
        if (bytes > 0)
        {
            _ = Interlocked.Add(ref _downloadBytes, bytes);
        }

        WriteLine($"download,{Escape(url)},{bytes},,{elapsedMs:F0},");
    }

    /// <summary>
    /// 记一次图片解码完成.
    /// </summary>
    /// <param name="url">地址.</param>
    /// <param name="decodeWidth">解码宽度.</param>
    /// <param name="decodeHeight">解码高度.</param>
    /// <param name="elapsedMs">从设置地址到加载完成的耗时.</param>
    public static void RecordLoaded(string? url, int decodeWidth, int decodeHeight, double elapsedMs)
    {
        if (!IsEnabled)
        {
            return;
        }

        _ = Interlocked.Increment(ref _loadedCount);
        WriteLine($"loaded,{Escape(url)},,{decodeWidth},{decodeHeight},{elapsedMs:F0},");
    }

    /// <summary>
    /// 记一次图片加载失败.
    /// </summary>
    /// <param name="url">地址.</param>
    /// <param name="message">失败原因.</param>
    public static void RecordFailed(string? url, string? message)
    {
        if (!IsEnabled)
        {
            return;
        }

        var count = Interlocked.Increment(ref _failedCount);
        WriteLine($"failed,{Escape(url)},,,,0,{Escape(message)}");

        // 失败日志节流：前 5 条全打，之后每 50 条一条汇总，避免风暴刷屏.
        var shouldLog = count <= 5 || Interlocked.Increment(ref _failedLogged) % 50 == 0;
        if (shouldLog)
        {
            Serilog.Log.Information(
                "图片加载失败（第 {Count} 次）：{Url} {Message}",
                count,
                url,
                message);
        }
    }

    /// <summary>
    /// 把累计值打成一行汇总，方便对着 CSV 看规模.
    /// </summary>
    public static void LogSummary()
    {
        if (!IsEnabled)
        {
            return;
        }

        Serilog.Log.Information(
            "图片诊断汇总：下载 {Downloads} 次 / {Megabytes:F1} MB，解码完成 {Loaded} 次，失败 {Failed} 次",
            _downloadCount,
            _downloadBytes / 1024d / 1024d,
            _loadedCount,
            _failedCount);
    }

    private static void WriteLine(string line)
    {
        if (_csvPath is null)
        {
            return;
        }

        try
        {
            lock (FileLock)
            {
                File.AppendAllText(_csvPath, $"{DateTime.Now:HH:mm:ss.fff},{line}{Environment.NewLine}");
            }
        }
        catch
        {
            // 写日志失败不该影响图片加载.
        }
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>
    /// 给 HttpClient 用的计时包装.
    /// </summary>
    internal sealed class TimingHandler : DelegatingHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TimingHandler"/> class.
        /// </summary>
        /// <param name="inner">内层 handler.</param>
        public TimingHandler(HttpMessageHandler inner)
            : base(inner)
        {
        }

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            RecordDownload(
                request.RequestUri?.ToString(),
                response.Content?.Headers.ContentLength ?? -1,
                watch.Elapsed.TotalMilliseconds);
            return response;
        }
    }
}
