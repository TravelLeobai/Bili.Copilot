// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.WinUIKernel.Share.Base;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 搜索首页，界面上只有一个搜索框，背景取自 B 站装扮包.
/// </summary>
public sealed partial class SearchHomePage : SearchHomePageBase
{
    private MediaPlayer? _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchHomePage"/> class.
    /// </summary>
    public SearchHomePage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) => ApplyThemeBackground();

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        VideoBackground.SetMediaPlayer(null);
    }

    private void ApplyThemeBackground()
    {
        // 主题包解压后放在 Assets\Theme\<id>\ 下，bin 里有一份（csproj 里配了 CopyToOutputDirectory），
        // 所以走 AppContext.BaseDirectory 的绝对路径，不依赖 ms-appx / PRI.
        var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Theme", "chuangzuo");

        var staticPath = System.IO.Path.Combine(folder, "head_myself_squared_bg.png");
        if (System.IO.File.Exists(staticPath))
        {
            StaticBackground.Source = new BitmapImage(new Uri(staticPath));
        }

        var videoPath = System.IO.Path.Combine(folder, "head_myself_mp4_bg.mp4");
        if (!System.IO.File.Exists(videoPath))
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
            // 视频背景失败就保持静态图，不影响搜索功能.
            Serilog.Log.Warning(ex, "搜索页动态背景加载失败");
            VideoBackground.Opacity = 0;
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