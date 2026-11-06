// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 搜索首页，界面上只有一个搜索框，背景取自 B 站装扮包.
/// </summary>
public sealed partial class SearchHomePage : Page
{
    // 装扮包解压后放在 Assets\Theme\<id>\ 下，命名规则是 B 站原生的那套：
    // head_bg.jpg / head_myself_mp4_bg.mp4 / head_myself_squared_bg.png.
    private const string ThemeFolder = "ms-appx:///Assets/Theme/chuangzuo";

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

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // 静态底图先铺上，视频起不来也不至于空着.
        StaticBackground.Source = new BitmapImage(new Uri($"{ThemeFolder}/head_myself_squared_bg.png"));

        try
        {
            _player = new MediaPlayer
            {
                IsLoopingEnabled = true,
                IsMuted = true,
                AutoPlay = true,
            };
            _player.Source = MediaSource.CreateFromUri(new Uri($"{ThemeFolder}/head_myself_mp4_bg.mp4"));
            VideoBackground.SetMediaPlayer(_player);
            VideoBackground.Opacity = 1;
        }
        catch (Exception ex)
        {
            // 视频背景失败就保持静态图，不影响搜索功能.
            Serilog.Log.Warning(ex, "搜索页动态背景加载失败");
            VideoBackground.Opacity = 0;
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        VideoBackground.SetMediaPlayer(null);
    }
}
