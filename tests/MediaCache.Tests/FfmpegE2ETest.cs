// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Net.Http.Headers;
using BiliCopilot.UI.Services.Media;

namespace MediaCache.Tests;

/// <summary>
/// 用仓库自带的 ffmpeg（lavf，和 mpv 同一套 HTTP 客户端）真正播一遍代理地址.
/// 这一步验证的是「真实播放器能从这个 HTTP 服务取流」，不是只验证我自己的 HttpClient.
/// </summary>
internal static class FfmpegE2ETest
{
    private const int SampleSize = 4 * 1024 * 1024;

    private static string? _samplePath;

    public static void Register()
    {
        TestHost.Add("ffmpeg：真实 lavf 客户端从代理地址读完整片（含 seek）", async () =>
        {
            var ffmpeg = ResolveFfmpeg();
            if (ffmpeg is null)
            {
                Console.WriteLine("        SKIP 找不到 ffmpeg.exe（设置 FFMPEG_PATH 或放 Assets/ffmpeg/x64）");
                return;
            }

            var sample = EnsureSample(ffmpeg);
            if (sample is null)
            {
                Console.WriteLine("        SKIP 本机 ffmpeg 生成不出测试用 mp4（缺编码器）");
                return;
            }

            var payload = await File.ReadAllBytesAsync(sample);
            var cdn = new FakeCdn(payload);
            var proxy = new MediaBufferProxy(new MediaProxyOptions
            {
                BlockSize = 64 * 1024,
                CapacityBytes = SampleSize,
                SessionToken = "ffmpegsession0123456789ab",
                UpstreamTimeout = TimeSpan.FromSeconds(20),
            });
            proxy.Start();
            try
            {
                var identity = MediaStreamIdentity.Create(
                    "BV1ffmpeg",
                    "3003",
                    "80",
                    "7",
                    "video/mp4",
                    payload.Length,
                    null,
                    null,
                    cdn.Url + "?deadline=1");
                using var registration = proxy.Register(new MediaStreamSourceDescriptor(cdn.Url, identity)
                {
                    Referer = "https://www.bilibili.com",
                    UserAgent = "MediaCache.Tests/1.0",
                    DisplayName = "ffmpeg 端到端",
                    OwnerKey = "ffmpeg",
                });

                var (exitCode, output) = await RunFfmpegAsync(ffmpeg, ["-v", "error", "-i", registration.ProxyUrl, "-f", "null", "-"]);
                Assert.Equal(0, exitCode, "全程播放应成功（ffmpeg 输出：" + Trim(output) + "）");

                // seek 到 1 秒：走的是文件中间，必须由代理按块补齐（而不是只读开头）.
                var (seekExit, seekOutput) = await RunFfmpegAsync(ffmpeg, ["-v", "error", "-ss", "1", "-i", registration.ProxyUrl, "-f", "null", "-"]);
                Assert.Equal(0, seekExit, "seek 到 1 秒应成功（ffmpeg 输出：" + Trim(seekOutput) + "）");

                var stats = proxy.GetStatistics();
                Assert.True(stats.Cache.MissBytes > 0, "应有从上游拉的字节");
                Assert.True(stats.Requests >= 2, "ffmpeg 应至少发起 2 次请求，实际 " + stats.Requests);
                Console.WriteLine("        ffmpeg 通过代理读完整片 + seek 均成功；" + stats);

                // 同一地址再读一次：这次应当大量命中缓存（上游请求不再增长）.
                var upstreamBefore = cdn.RequestCount;
                var (againExit, againOutput) = await RunFfmpegAsync(ffmpeg, ["-v", "error", "-i", registration.ProxyUrl, "-f", "null", "-"]);
                Assert.Equal(0, againExit, "第二次播放应成功（ffmpeg 输出：" + Trim(againOutput) + "）");
                var statsAgain = proxy.GetStatistics();
                Console.WriteLine($"        第二次播放后上游请求数 {upstreamBefore} → {cdn.RequestCount}，命中字节 {statsAgain.Cache.HitBytes / 1024} KiB");
                Assert.True(
                    statsAgain.Cache.HitBytes > stats.Cache.HitBytes,
                    "第二次播放应产生新的缓存命中字节");
            }
            finally
            {
                proxy.Dispose();
                cdn.Dispose();
            }
        });
    }

    private static string Trim(string text)
    {
        var single = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 240 ? single : single[..240];
    }

    private static string? ResolveFfmpeg()
    {
        var fromEnv = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var candidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Desktop", "BiliCopilot.UI", "Assets", "ffmpeg", "x64", "ffmpeg.exe"));
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? EnsureSample(string ffmpeg)
    {
        if (_samplePath is not null && File.Exists(_samplePath))
        {
            return _samplePath;
        }

        var folder = Path.Combine(Path.GetTempPath(), "bilicopilot-media-proxy-tests");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "sample.mp4");
        if (File.Exists(path) && new FileInfo(path).Length > 1024)
        {
            _samplePath = path;
            return path;
        }

        // 用最小参数造一段 2 秒的 mp4（faststart 让 moov 在前，模拟 B 站 dash 的真实布局）.
        var (exitCode, _) = RunFfmpegSync(ffmpeg, [
            "-y",
            "-f", "lavfi", "-i", "testsrc=size=320x180:rate=15",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
            "-t", "2",
            "-c:v", "mpeg4", "-q:v", "5",
            "-c:a", "aac", "-b:a", "64k",
            "-movflags", "+faststart",
            path,
        ]);
        if (exitCode != 0 || !File.Exists(path))
        {
            return null;
        }

        _samplePath = path;
        return path;
    }

    private static (int ExitCode, string Output) RunFfmpegSync(string ffmpeg, string[] args)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return (-1, "无法启动 ffmpeg");
        }

        var stderr = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.HasExited ? process.ExitCode : -1, stderr);
    }

    private static async Task<(int ExitCode, string Output)> RunFfmpegAsync(string ffmpeg, string[] args)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return (-1, "无法启动 ffmpeg");
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return (-1, "ffmpeg 超时");
        }

        var stderr = await stderrTask;
        _ = await stdoutTask;
        return (process.ExitCode, stderr);
    }
}
