// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// 视频卡片的 Reactor 宿主，模板里只留一行，内容交给 <see cref="VideoCardBody"/> 排.
/// </summary>
public sealed partial class VideoCardReactorHost : UserControl
{
    /// <summary>
    /// 卡片数据.
    /// </summary>
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(VideoItemViewModel), typeof(VideoCardReactorHost),
        new PropertyMetadata(null, OnViewModelChanged));

    /// <summary>
    /// 版式.
    /// </summary>
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(VideoCardLayout), typeof(VideoCardReactorHost),
        new PropertyMetadata(VideoCardLayout.Recommend, OnViewModelChanged));

    private ReactorHostControl? _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoCardReactorHost"/> class.
    /// </summary>
    public VideoCardReactorHost() => InitializeComponent();

    /// <summary>
    /// 卡片数据.
    /// </summary>
    public VideoItemViewModel? ViewModel
    {
        get => (VideoItemViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// 版式.
    /// </summary>
    public VideoCardLayout Layout
    {
        get => (VideoCardLayout)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => (sender as VideoCardReactorHost)?.Mount();

    private void Mount()
    {
        if (ViewModel is null)
        {
            if (_host is not null)
            {
                RootGrid.Children.Remove(_host);
                _host = null;
            }

            return;
        }

        if (_host is null)
        {
            _host = new ReactorHostControl();
            RootGrid.Children.Add(_host);
        }

        _host.Mount(new VideoCardBody(ViewModel, Layout));
    }
}
