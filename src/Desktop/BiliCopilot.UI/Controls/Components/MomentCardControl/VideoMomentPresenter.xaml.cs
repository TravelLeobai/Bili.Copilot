// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// 视频动态卡片控件.
/// 版式交给 <see cref="MomentVideoCard"/> 用 Reactor 渲染.
/// </summary>
public sealed partial class VideoMomentPresenter : MomentCardPresenter
{
    /// <summary>
    /// 是否按标准视频比例显示封面.
    /// </summary>
    public static readonly DependencyProperty PreserveCoverAspectRatioProperty = DependencyProperty.Register(
        nameof(PreserveCoverAspectRatio), typeof(bool), typeof(VideoMomentPresenter),
        new PropertyMetadata(false, OnCoverAspectRatioChanged));

    private ReactorHostControl? _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoMomentPresenter"/> class.
    /// </summary>
    public VideoMomentPresenter()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 是否保持 16:9 封面比例.
    /// </summary>
    public bool PreserveCoverAspectRatio
    {
        get => (bool)GetValue(PreserveCoverAspectRatioProperty);
        set => SetValue(PreserveCoverAspectRatioProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(MomentItemViewModel? oldValue, MomentItemViewModel? newValue)
        => Mount();

    private static void OnCoverAspectRatioChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((VideoMomentPresenter)sender).Mount();

    private void OnLoaded(object sender, RoutedEventArgs e) => Mount();

    private void Mount()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (_host is null)
        {
            _host = new ReactorHostControl();
            RootGrid.Children.Add(_host);
        }

        _host.Mount(new MomentVideoCard(ViewModel, PreserveCoverAspectRatio));
    }
}
