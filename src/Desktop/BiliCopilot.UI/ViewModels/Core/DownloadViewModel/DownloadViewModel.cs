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
        _downloadPath = ResolveDownloadPath(localDownloadFolder, logger);

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
    /// 决定下载目录：配置的优先，其次我的视频，最后退回应用自己的 LocalState。
    /// 任何一步建不出来都往下退，绝不从构造函数里抛出去.
    /// </summary>
    /// <param name="configured">设置里配置的目录.</param>
    /// <param name="logger">日志.</param>
    /// <returns>可用的下载目录.</returns>
    private static string ResolveDownloadPath(string configured, ILogger? logger)
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

        // 兜底：包自己的 LocalState，一定可写.
        candidates.Add(Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
            "Downloads"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (!Path.IsPathRooted(candidate))
                {
                    continue;
                }

                Directory.CreateDirectory(candidate);
                if (Directory.Exists(candidate))
                {
                    if (!string.Equals(candidate, configured, StringComparison.OrdinalIgnoreCase))
                    {
                        logger?.LogInformation("下载目录改用：{Path}", candidate);
                    }

                    return candidate;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "下载目录不可用：{Path}", candidate);
            }
        }

        logger?.LogWarning("所有下载目录都不可用，退回包 LocalFolder");
        return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
    }
}
