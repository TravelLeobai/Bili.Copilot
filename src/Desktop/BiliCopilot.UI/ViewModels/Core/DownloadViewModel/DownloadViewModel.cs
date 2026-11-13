// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.ViewModels;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 下载视图模型.
/// </summary>
public sealed partial class DownloadViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadViewModel"/> class.
    /// </summary>
    public DownloadViewModel(
        ILogger<DownloadViewModel> logger,
        IBiliTokenResolver tokenResolver,
        IBiliCookiesResolver cookiesResolver)
    {
        _logger = logger;
        _tokenResolver = tokenResolver;
        _cookiesResolver = cookiesResolver;
        _bbdownPath = this.Get<AppViewModel>().BBDownPath;
        _ffmpegPath = this.Get<AppViewModel>().FFmpegPath;
        // 打包应用里 Environment.SpecialFolder.MyVideos 可能返回空串，
        // Path.Combine("", "Bili Downloads") 就变成相对路径，按进程工作目录解析成
        // C:\WINDOWS\system32\Bili Downloads → UnauthorizedAccessException；
        // 这段在 PlayerViewModel 的初始化链里，一炸就是"进播放器闪退".
        var localDownloadFolder = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DownloadFolder, string.Empty);
        // 性能探针（三级）：下载目录解析会在 UI 线程上真的去建目录（网络盘掉线时会阻塞），记检查点.
        Services.PerfWidgetMountTrace.Mark("download-path-resolve-start");
        _downloadPath = ResolveDownloadPath(localDownloadFolder, logger);
        Services.PerfWidgetMountTrace.Mark("download-path-resolve-end");

        _openFolderAfterDownload = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.OpenFolderAfterDownload, true);
    }

    /// <summary>
    /// 初始化基础数据.
    /// </summary>
    public void InitializeMetas(
        string url,
        IReadOnlyCollection<PlayerFormatInformation> formats,
        IReadOnlyCollection<VideoPart>? parts = default,
        int currentPart = 1)
    {
        _currentUrl = url;
        _currentPartIndex = currentPart;
        Formats = [.. formats];
        Parts = parts is null ? default : [.. parts];
        MetaInitialized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 初始化基础数据.
    /// </summary>
    public void InitializeMetas(
        string epUrl,
        string ssUrl,
        IReadOnlyCollection<PlayerFormatInformation> formats,
        IReadOnlyCollection<EpisodeInformation>? episodes = default,
        int currentEpisode = 1)
    {
        _currentUrl = epUrl;
        _parentUrl = ssUrl;
        _currentPartIndex = currentEpisode;
        Formats = [.. formats];
        Episodes = episodes is null ? default : [.. episodes];
        MetaInitialized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 清除数据.
    /// </summary>
    public void Clear()
    {
        _currentUrl = string.Empty;
        _parentUrl = string.Empty;
        _currentPartIndex = 0;
        Formats = null;
        Parts = null;
        Episodes = null;
    }

    private static bool UseExternal()
        => SettingsToolkit.ReadLocalSetting(SettingNames.UseExternalBBDown, false);

    /// <summary>
    /// 下载视频.
    /// </summary>
    [RelayCommand]
    private void DownloadWithFormat(PlayerFormatInformation? format = default)
    {
        var basicCommand = GetBasicCommand();
        if (_currentPartIndex > 1 && !_currentUrl.Contains("/ep"))
        {
            basicCommand += $" --select-page {_currentPartIndex}";
        }

        var quality = UseExternal() ? format.Description : format.Quality.ToString();
        var command = $"{basicCommand} -q \"{quality}\" \"{_currentUrl}\"";
        LaunchDownloadProcess(command);
    }

    [RelayCommand]
    private void DownloadCover()
    {
        var command = $"{GetBasicCommand(false)} --cover-only \"{_currentUrl}\"";
        LaunchDownloadProcess(command);
    }

    [RelayCommand]
    private void DownloadDanmaku()
    {
        var command = $"{GetBasicCommand(false)} --danmaku-only \"{_currentUrl}\"";
        LaunchDownloadProcess(command);
    }

    [RelayCommand]
    private void DownloadAudioOnly()
    {
        var command = $"{GetBasicCommand(false)} --audio-only \"{_currentUrl}\"";
        LaunchDownloadProcess(command);
    }

    [RelayCommand]
    private void DownloadSubtitle()
    {
        var isAISubtitleFiltered = SettingsToolkit.ReadLocalSetting(SettingNames.FilterAISubtitle, true);
        var subCommand = isAISubtitleFiltered ? "--sub-only" : "--sub-only --skip-ai False";
        var command = $"{GetBasicCommand(false)} {subCommand} \"{_currentUrl}\"";
        LaunchDownloadProcess(command);
    }

    [RelayCommand]
    private void BatchDownloadAllParts()
    {
        var url = _currentUrl.Contains("/ep") ? _parentUrl : _currentUrl;
        var basicCommand = GetBasicCommand();
        basicCommand += " --select-page ALL";
        var command = $"{basicCommand} \"{url}\"";
        LaunchDownloadProcess(command);
    }

    [RelayCommand]
    private void BatchDownloadSelectedParts(string selection)
    {
        var url = _currentUrl.Contains("/ep") ? _parentUrl : _currentUrl;
        var basicCommand = GetBasicCommand();
        basicCommand += $" --select-page {selection}";
        var command = $"{basicCommand} \"{url}\"";
        LaunchDownloadProcess(command);
    }

    private string GetBasicCommand(bool danmakuEnabled = true)
    {
        var token = _tokenResolver.GetToken().AccessToken;
        var cookie = _cookiesResolver.GetCookieString();
        var preferCodec = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.PreferCodec, PreferCodecType.H264);
        var codec = preferCodec switch
        {
            PreferCodecType.H264 => "avc",
            PreferCodecType.H265 => "hevc",
            _ => "av1",
        };

        var cmd = $"-e {codec} --work-dir \"{_downloadPath}\" -ua \"{VideoUserAgent}\"";
        var copyCommandOnly = SettingsToolkit.ReadLocalSetting(SettingNames.OnlyCopyCommandWhenDownload, false);
        var withoutCredential = SettingsToolkit.ReadLocalSetting(SettingNames.WithoutCredentialWhenGenDownloadCommand, false);
        if (!UseExternal())
        {
            cmd += $" -app --ffmpeg-path \"{_ffmpegPath}\"";
        }

        if (!(UseExternal() && withoutCredential))
        {
            cmd += $" --cookie \"{cookie}\" -token \"{token}\"";
        }

        if (danmakuEnabled)
        {
            var alsoDownDanmaku = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DownloadWithDanmaku, false);
            if (alsoDownDanmaku)
            {
                cmd += " -dd";
            }
        }

        return cmd;
    }

    private void LaunchDownloadProcess(string command)
    {
        var useExternalBBDown = SettingsToolkit.ReadLocalSetting(SettingNames.UseExternalBBDown, false);
        var copyCommandOnly = SettingsToolkit.ReadLocalSetting(SettingNames.OnlyCopyCommandWhenDownload, false);
        var fileName = useExternalBBDown ? "BBDown" : _bbdownPath;
        if (useExternalBBDown && copyCommandOnly)
        {
            var cmd = $"{fileName} {command}";
            var dp = new DataPackage();
            dp.SetText(cmd);
            Clipboard.SetContent(dp);
            this.Get<AppViewModel>().ShowTipCommand.Execute((ResourceToolkit.GetLocalizedString(StringNames.Copied), InfoType.Success));
            return;
        }

        _ = Task.Run(() =>
        {
            var startInfo = new ProcessStartInfo(fileName, command);
            var process = Process.Start(startInfo);
            if (_openFolderAfterDownload)
            {
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    Process.Start("explorer.exe", _downloadPath);
                }
            }
        });
    }

    /// <summary>
    /// 解析好的下载目录（一次运行只探一次）.
    /// </summary>
    private static string? _cachedDownloadPath;

    private static readonly object DownloadPathLock = new();

    /// <summary>
    /// 决定下载目录：配置的优先，其次我的视频，最后退回应用自己的 LocalState。
    /// <para>
    /// 这段**绝不能无条件在 UI 线程上做 I/O**：实测配置里留着已断开的网络盘
    /// （G:\Videos\Bili Downloads，映射到 \\192.168.2.126\pc）时，
    /// Directory.CreateDirectory 会固定卡 21.030 秒才抛「找不到网络路径」，
    /// 而它是在播放器初始化链（VideoConnectorViewModel 构造）里同步调用的，
    /// 于是点视频后 UI 线程被按住 21 秒 —— 用户看到的就是白屏卡死。
    /// </para>
    /// <para>
    /// 所以这里只在 UI 线程上等最多 300ms：够本地磁盘瞬间返回；
    /// 网络盘/掉线盘直接走兜底目录，探测放到后台继续跑，成功了下次启动就用上.
    /// </para>
    /// </summary>
    /// <param name="configured">设置里配置的目录.</param>
    /// <param name="logger">日志.</param>
    /// <returns>可用的下载目录.</returns>
    private static string ResolveDownloadPath(string configured, ILogger? logger)
    {
        lock (DownloadPathLock)
        {
            if (_cachedDownloadPath is not null)
            {
                return _cachedDownloadPath;
            }
        }

        // 兜底目录（本地 LocalState，建起来是毫秒级），先准备好.
        var fallback = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Downloads");

        var probe = Task.Run(() => ProbeDownloadPath(configured, logger));

        // UI 线程最多等 300ms；本地盘正常情况远小于这个数.
        if (probe.Wait(TimeSpan.FromMilliseconds(300)) && probe.Result is { } resolved)
        {
            lock (DownloadPathLock)
            {
                _cachedDownloadPath = resolved;
            }

            return resolved;
        }

        // 超时（典型就是掉线的网络盘）：先用兜底目录，别让播放器初始化干等.
        logger?.LogWarning("下载目录探测超过 300ms（多半是掉线的网络盘），先用兜底目录：{Path}", fallback);
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "兜底下载目录也建不出来：{Path}", fallback);
        }

        _ = probe.ContinueWith(
            t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is { } late)
                {
                    lock (DownloadPathLock)
                    {
                        _cachedDownloadPath = late;
                    }

                    Serilog.Log.Information("下载目录后台探测完成：{Path}", late);
                }
            },
            TaskScheduler.Default);

        lock (DownloadPathLock)
        {
            if (_cachedDownloadPath is null)
            {
                _cachedDownloadPath = fallback;
            }

            return _cachedDownloadPath;
        }
    }

    /// <summary>
    /// 真正去试每个候选目录（可能很慢，一律在后台线程跑）.
    /// </summary>
    /// <param name="configured">设置里配置的目录.</param>
    /// <param name="logger">日志.</param>
    /// <returns>可用的下载目录；全不可用时返回 <c>null</c>.</returns>
    private static string? ProbeDownloadPath(string configured, ILogger? logger)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrWhiteSpace(videos))
        {
            candidates.Add(Path.Combine(videos, "Bili Downloads"));
        }

        candidates.Add(Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Downloads"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (!Path.IsPathRooted(candidate))
                {
                    continue;
                }

                Directory.CreateDirectory(candidate);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                if (!string.Equals(candidate, configured, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogInformation("下载目录改用：{Path}", candidate);
                }

                return candidate;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "下载目录不可用：{Path}", candidate);
            }
        }

        return null;
    }
}

