// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Specialized;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 直播弹幕浮层：分轨道自右向左滚动.
/// </summary>
public sealed partial class LiveDanmakuOverlay : UserControl
{
    private const double LaneHeight = 30;
    private const double Speed = 170;
    private PlayerViewModel? _viewModel;
    private int _laneIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveDanmakuOverlay"/> class.
    /// </summary>
    public LiveDanmakuOverlay()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 播放器视图模型.
    /// </summary>
    public PlayerViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value))
            {
                return;
            }

            if (_viewModel is not null)
            {
                _viewModel.LiveInteractions.CollectionChanged -= OnInteractionsChanged;
            }

            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.LiveInteractions.CollectionChanged += OnInteractionsChanged;
            }
        }
    }

    private void OnInteractionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        foreach (var item in e.NewItems)
        {
            if (item is LiveInteractionViewModel { Kind: LiveInteractionKind.Danmaku } danmaku)
            {
                AddDanmaku(danmaku.Text);
            }
        }
    }

    private void AddDanmaku(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var block = new TextBlock
        {
            Text = text,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            IsHitTestVisible = false,
            Shadow = new ThemeShadow(),
            MaxWidth = Math.Max(200, ActualWidth * 0.7),
        };

        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var blockWidth = block.DesiredSize.Width;
        var laneCount = Math.Max(1, (int)(ActualHeight / LaneHeight));
        var lane = _laneIndex++ % laneCount;
        Canvas.SetTop(block, lane * LaneHeight + 8);

        var transform = new TranslateTransform { X = ActualWidth };
        block.RenderTransform = transform;
        RootCanvas.Children.Add(block);

        var duration = TimeSpan.FromSeconds((ActualWidth + blockWidth) / Speed);
        var animation = new DoubleAnimation
        {
            From = ActualWidth,
            To = -blockWidth,
            Duration = duration,
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            RootCanvas.Children.Remove(block);
            storyboard.Stop();
        };

        storyboard.Begin();

        // 控制同屏数量，避免长时间挂机后元素堆积.
        while (RootCanvas.Children.Count > 60)
        {
            RootCanvas.Children.RemoveAt(0);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LiveInteractions.CollectionChanged -= OnInteractionsChanged;
        }
    }
}