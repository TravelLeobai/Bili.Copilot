// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.WinUIKernel.Share.Base;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 搜索首页，界面上只有一个搜索框.
/// 静态背景用本地皮肤包里的图，动态背景从 B 站 CDN 拉 mp4 缓存到本地再播.
/// </summary>
public sealed partial class SearchHomePage : SearchHomePageBase
{
    // 头像和静态背景都来自本地皮肤包，只有视频从网上拿.
    private const string VideoUrl = "https://i0.hdslb.com/bfs/garb/item/d72c45e284c6ebb234066de66960476a0e91640e.mp4";
    private const string ThemeFolderName = "skin";

    private MediaPlayer? _player;
    private bool _videoTried;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchHomePage"/> class.
    /// </summary>
    public SearchHomePage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ApplyStaticBackground();
        await TryStartVideoAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        VideoBackground.SetMediaPlayer(null);
    }

    private void ApplyStaticBackground()
    {
        var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Theme", ThemeFolderName);

        // 皮肤包里同时有 jpg 和 png 两版背景，优先用小的那张.
        var jpgPath = System.IO.Path.Combine(folder, "head_myself_squared_bg.jpg");
        var pngPath = System.IO.Path.Combine(folder, "head_myself_squared_bg.png");
        var staticPath = System.IO.File.Exists(jpgPath) ? jpgPath : pngPath;
        if (System.IO.File.Exists(staticPath))
        {
            StaticBackground.Source = new BitmapImage(new Uri(staticPath));
        }
    }

    private async System.Threading.Tasks.Task TryStartVideoAsync()
    {
        if (_videoTried)
        {
            return;
        }

        _videoTried = true;
        var videoPath = await EnsureVideoAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(videoPath))
        {
            return;
        }

        try
        {
            _player ??= new MediaPlayer
            {
                IsLoopingEnabled = true,
                IsMuted = true,
                AutoPlay = true,
            };
            _player.Source = MediaSource.CreateFromUri(new Uri(videoPath));
            VideoBackground.SetMediaPlayer(_player);
            VideoBackground.Opacity = 1;
            _player.Play();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "搜索页动态背景播放失败");
            VideoBackground.Opacity = 0;
        }
    }

    /// <summary>
    /// 把装扮包里的视频拉到本地缓存，拉过一次之后直接复用.
    /// </summary>
    private static async System.Threading.Tasks.Task<string?> EnsureVideoAsync()
    {
        try
        {
            var cacheDir = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "ThemeCache");
            System.IO.Directory.CreateDirectory(cacheDir);

            var target = System.IO.Path.Combine(cacheDir, "head_myself_mp4_bg.mp4");
            if (System.IO.File.Exists(target))
            {
                var length = new System.IO.FileInfo(target).Length;
                if (length > 100_000)
                {
                    return target;
                }
            }

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            http.Timeout = TimeSpan.FromSeconds(60);

            var bytes = await http.GetByteArrayAsync(VideoUrl).ConfigureAwait(false);
            if (bytes.Length < 100_000)
            {
                Serilog.Log.Warning("动态背景下载内容过小：{Size}", bytes.Length);
                return null;
            }

            await System.IO.File.WriteAllBytesAsync(target, bytes).ConfigureAwait(false);
            return target;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "动态背景下载失败");
            return null;
        }
    }
}

/// <summary>
/// 搜索首页基类.
/// </summary>
public abstract class SearchHomePageBase : LayoutPageBase<SearchBoxViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SearchHomePageBase"/> class.
    /// </summary>
    protected SearchHomePageBase() => ViewModel = this.Get<SearchBoxViewModel>();
}