// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.WinUIKernel.Share.Base;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace BiliCopilot.UI.Pages;

/// <summary>
/// 搜索首页，界面上只有一个搜索框加右下角的背景设置入口.
/// 背景支持：B 站主题 JSON 导入、本地图片、透明；导入过的都留在本地可选.
/// </summary>
public sealed partial class SearchHomePage : SearchHomePageBase
{
    private MediaPlayer? _player;
    private bool _isBusy;

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
        RefreshBackgroundList();
        ApplyCurrentBackground();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        StopVideo();
    }

    private void StopVideo()
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        VideoBackground.SetMediaPlayer(null);
        VideoBackground.Opacity = 0;
    }

    /// <summary>
    /// 按当前选中的背景刷新画面.
    /// </summary>
    private void ApplyCurrentBackground()
    {
        StopVideo();

        var entry = BackgroundLibrary.Current;
        if (entry.IsTransparent)
        {
            StaticBackground.Source = null;
            StaticBackground.Visibility = Visibility.Collapsed;
            Scrim.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return;
        }

        StaticBackground.Visibility = Visibility.Visible;
        Scrim.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0x40, 0, 0, 0));

        if (!string.IsNullOrEmpty(entry.ImagePath) && System.IO.File.Exists(entry.ImagePath))
        {
            StaticBackground.Source = new BitmapImage(new Uri(entry.ImagePath));
        }
        else
        {
            StaticBackground.Source = null;
        }

        if (!string.IsNullOrEmpty(entry.VideoPath) && System.IO.File.Exists(entry.VideoPath))
        {
            try
            {
                _player = new MediaPlayer
                {
                    IsLoopingEnabled = true,
                    IsMuted = true,
                    AutoPlay = true,
                };
                _player.Source = MediaSource.CreateFromUri(new Uri(entry.VideoPath));
                VideoBackground.SetMediaPlayer(_player);
                VideoBackground.Opacity = 1;
                _player.Play();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "动态背景播放失败");
                VideoBackground.Opacity = 0;
            }
        }
    }

    /// <summary>
    /// 重建背景选择列表.
    /// </summary>
    private void RefreshBackgroundList()
    {
        BackgroundListPanel.Children.Clear();
        var currentId = BackgroundLibrary.CurrentId;

        foreach (var entry in BackgroundLibrary.Load())
        {
            var row = new Grid { Height = 40, ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pick = new Button
            {
                Content = (entry.Id == currentId ? "✓ " : string.Empty) + entry.Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = entry.Id,
            };
            pick.Click += OnBackgroundPickClick;

            row.Children.Add(pick);
            if (!entry.IsTransparent)
            {
                var del = new Button
                {
                    Content = "✕",
                    Width = 36,
                    Tag = entry.Id,
                };
                del.Click += OnBackgroundDeleteClick;
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
            }

            BackgroundListPanel.Children.Add(row);
        }
    }

    private void OnBackgroundPickClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        BackgroundLibrary.CurrentId = id;
        ApplyCurrentBackground();
        RefreshBackgroundList();
    }

    private void OnBackgroundDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        BackgroundLibrary.Remove(id);
        ApplyCurrentBackground();
        RefreshBackgroundList();
    }

    private async void OnImportThemeClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var path = await PickFileAsync(".json");
        if (path is null)
        {
            return;
        }

        _isBusy = true;
        StatusText.Text = "正在下载并解析主题包…";
        try
        {
            var entry = await BackgroundLibrary.ImportThemeAsync(path);
            BackgroundLibrary.CurrentId = entry.Id;
            ApplyCurrentBackground();
            RefreshBackgroundList();
            StatusText.Text = $"已导入：{entry.Name}";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "导入主题失败");
            StatusText.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async void OnImportImageClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var path = await PickFileAsync(".jpg", ".jpeg", ".png", ".webp", ".bmp");
        if (path is null)
        {
            return;
        }

        _isBusy = true;
        StatusText.Text = "正在保存图片…";
        try
        {
            var entry = BackgroundLibrary.ImportImage(path);
            BackgroundLibrary.CurrentId = entry.Id;
            ApplyCurrentBackground();
            RefreshBackgroundList();
            StatusText.Text = $"已导入：{entry.Name}";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "导入图片失败");
            StatusText.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>
    /// 弹文件选择器，返回选中文件的本地路径.
    /// </summary>
    private async System.Threading.Tasks.Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var window = this.Get<BiliCopilot.UI.ViewModels.Core.AppViewModel>()
            .Windows
            .OfType<BiliCopilot.UI.Forms.MainWindow>()
            .FirstOrDefault();
        if (window is null)
        {
            return null;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
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
