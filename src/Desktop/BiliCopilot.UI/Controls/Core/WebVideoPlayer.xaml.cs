// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;

namespace BiliCopilot.UI.Controls.Core;

/// <summary>
/// 基于 WebView2 的播放器.
/// 参考 BiliLite 的做法：把 www.bilibili.com 映射到本地播放页，由网页播放器负责解码与音画同步，
/// 宿主只通过 postMessage 收状态、通过 ExecuteScriptAsync 下指令.
/// </summary>
public sealed partial class WebVideoPlayer : UserControl
{
    private const string PlayerHost = "www.bilibili.com";
    private const string PlayerFolder = "WebPlayer";

    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _isInitialized;
    private string _lastState = "idle";

    /// <summary>
    /// Initializes a new instance of the <see cref="WebVideoPlayer"/> class.
    /// </summary>
    public WebVideoPlayer()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 播放位置变化.
    /// </summary>
    public event EventHandler<double>? PositionChanged;

    /// <summary>
    /// 媒体加载完成（给出时长）.
    /// </summary>
    public event EventHandler<double>? MediaLoaded;

    /// <summary>
    /// 播放结束.
    /// </summary>
    public event EventHandler? MediaEnded;

    /// <summary>
    /// 播放状态变化（playing / paused / buffering / end）.
    /// </summary>
    public event EventHandler<string>? PlaybackStateChanged;

    /// <summary>
    /// 播放出错.
    /// </summary>
    public event EventHandler<string>? PlaybackFailed;

    /// <summary>
    /// 播放引擎就绪（附带 Shaka 版本等信息）.
    /// </summary>
    public event EventHandler<string>? EngineReady;

    /// <summary>
    /// 当前播放位置（秒）.
    /// </summary>
    public double Position { get; private set; }

    /// <summary>
    /// 媒体时长（秒）.
    /// </summary>
    public double Duration { get; private set; } = -1;

    /// <summary>
    /// 最近一次播放状态.
    /// </summary>
    public string LastState => _lastState;

    /// <summary>
    /// 是否已经初始化.
    /// </summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// 用于网页播放器请求的 Cookie.
    /// </summary>
    public string CookieString { get; set; } = string.Empty;

    /// <summary>
    /// 载入音视频地址.
    /// </summary>
    public async Task LoadAsync(string? videoUrl, string? audioUrl)
    {
        await InitializeAsync();

        var payload = $"{{\"video\":{{\"url\":\"{EscapeJson(videoUrl)}\"}},\"audio\":{{\"url\":\"{EscapeJson(audioUrl)}\"}}}}";

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        Position = 0;
        Duration = -1;
        _lastState = "loading";
        PlayerView.CoreWebView2.Navigate($"https://{PlayerHost}/index.html?view=video&playData={encoded}");
    }

    private static string EscapeJson(string? value)
        => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// 暂停.
    /// </summary>
    public Task PauseAsync() => InvokeAsync("window.pause()");

    /// <summary>
    /// 继续播放.
    /// </summary>
    public Task ResumeAsync() => InvokeAsync("window.resume()");

    /// <summary>
    /// 跳转到指定位置.
    /// </summary>
    public Task SeekAsync(double seconds)
        => InvokeAsync($"window.seek({seconds.ToString("F3", CultureInfo.InvariantCulture)})");

    /// <summary>
    /// 设置音量（0-100）.
    /// </summary>
    public Task SetVolumeAsync(double volume)
        => InvokeAsync($"window.setVolume({(volume / 100d).ToString("F4", CultureInfo.InvariantCulture)})");

    /// <summary>
    /// 设置播放速度.
    /// </summary>
    public Task SetRateAsync(double rate)
        => InvokeAsync($"window.setRate({rate.ToString("F2", CultureInfo.InvariantCulture)})");

    /// <summary>
    /// 设置音画同步阈值.
    /// </summary>
    public Task SetSyncThresholdAsync(double small, double large)
        => InvokeAsync(
            $"window.setCheckAVSyncDiff({small.ToString("F2", CultureInfo.InvariantCulture)},{large.ToString("F2", CultureInfo.InvariantCulture)})");

    private async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        await _initializeLock.WaitAsync();
        try
        {
            if (_isInitialized)
            {
                return;
            }

            Environment.SetEnvironmentVariable(
                "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                "--autoplay-policy=no-user-gesture-required --disable-features=msWebOOUI,msPdfOOUI");

            var userDataFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "WebPlayerData");
            Directory.CreateDirectory(userDataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
            await PlayerView.EnsureCoreWebView2Async();

            var core = PlayerView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;

            var assetsFolder = Path.Combine(AppContext.BaseDirectory, "Assets", PlayerFolder);
            core.SetVirtualHostNameToFolderMapping(
                PlayerHost,
                assetsFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            _isInitialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private async Task ApplyCookiesAsync()
    {
        if (string.IsNullOrWhiteSpace(CookieString) || PlayerView.CoreWebView2 is null)
        {
            return;
        }

        var manager = PlayerView.CoreWebView2.CookieManager;
        foreach (var segment in CookieString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0 || index == segment.Length - 1)
            {
                continue;
            }

            var name = segment[..index];
            var value = segment[(index + 1)..];
            try
            {
                var cookie = manager.CreateCookie(name, value, ".bilibili.com", "/");
                manager.AddOrUpdateCookie(cookie);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }

        await Task.CompletedTask;
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var eventNode))
            {
                return;
            }

            var eventName = eventNode.GetString();
            var data = root.TryGetProperty("data", out var dataNode) ? dataNode : default;

            switch (eventName)
            {
                case "positionChanged":
                    if (data.ValueKind == JsonValueKind.Number && data.TryGetDouble(out var position))
                    {
                        Position = position;
                        PositionChanged?.Invoke(this, position);
                    }

                    break;
                case "loaded":
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("duration", out var durationNode))
                    {
                        Duration = durationNode.GetDouble();
                        MediaLoaded?.Invoke(this, Duration);
                    }

                    break;
                case "ended":
                    _lastState = "end";
                    PlaybackStateChanged?.Invoke(this, _lastState);
                    MediaEnded?.Invoke(this, EventArgs.Empty);
                    break;
                case "stateChanged":
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("state", out var stateNode))
                    {
                        _lastState = stateNode.GetString() ?? _lastState;
                        PlaybackStateChanged?.Invoke(this, _lastState);
                    }

                    break;
                case "engineReady":
                    var info = data.ValueKind == JsonValueKind.Object
                        ? $"shaka={data.GetProperty("shaka").GetString()}, video={data.GetProperty("videoPlayer")}, audio={data.GetProperty("audioPlayer")}"
                        : "unknown";
                    EngineReady?.Invoke(this, info);
                    break;
                case "error":
                    var message = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var messageNode)
                        ? messageNode.GetString()
                        : "unknown";
                    PlaybackFailed?.Invoke(this, message ?? "unknown");
                    break;
                default:
                    break;
            }
        }
            catch (Exception ex)
        {
            _ = ex;
        }
    }

    private async Task InvokeAsync(string script)
    {
        if (!_isInitialized || PlayerView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await PlayerView.CoreWebView2.ExecuteScriptAsync(script);
        }
            catch (Exception ex)
        {
            _ = ex;
        }
    }

    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            PlayerView.Close();
        }
            catch (Exception ex)
        {
            _ = ex;
        }
    }
}
