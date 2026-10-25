// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BiliCopilot.UI.Controls.Components;

/// <summary>
/// 仅在激活时绘制并更新颜色的流动彩虹边框.
/// </summary>
public sealed partial class AnimatedRainbowBorder : UserControl
{
    /// <summary>
    /// 标识 <see cref="IsActive"/> 依赖属性.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(AnimatedRainbowBorder),
        new PropertyMetadata(false, OnIsActiveChanged));

    private readonly LinearGradientBrush _brush;
    private readonly List<GradientStop> _stops;
    private DispatcherQueueTimer? _timer;
    private double _phase;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimatedRainbowBorder"/> class.
    /// </summary>
    public AnimatedRainbowBorder()
    {
        IsHitTestVisible = false;
        _stops = Enumerable.Range(0, 7)
            .Select(index => new GradientStop { Offset = index / 6d })
            .ToList();
        _brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };

        foreach (var stop in _stops)
        {
            _brush.GradientStops.Add(stop);
        }

        var border = new Border { BorderBrush = _brush };
        border.SetBinding(Border.BorderThicknessProperty, new Binding { Source = this, Path = new PropertyPath(nameof(BorderThickness)) });
        border.SetBinding(Border.CornerRadiusProperty, new Binding { Source = this, Path = new PropertyPath(nameof(CornerRadius)) });
        Content = border;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateColors();
    }

    /// <summary>
    /// 获取或设置是否运行动画.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AnimatedRainbowBorder)d).UpdateTimerState();

    private void OnLoaded(object sender, RoutedEventArgs e)
        => UpdateTimerState();

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => StopTimer();

    private void UpdateTimerState()
    {
        if (!IsLoaded || !IsActive)
        {
            StopTimer();
            return;
        }

        _timer ??= DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        }
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        _phase = (_phase + 0.008d) % 1d;
        UpdateColors();
    }

    private void UpdateColors()
    {
        for (var index = 0; index < _stops.Count; index++)
        {
            _stops[index].Color = FromHsv((_phase + (index / 6d)) % 1d);
        }
    }

    private static Color FromHsv(double hue)
    {
        var section = hue * 6d;
        var fraction = section - Math.Floor(section);
        var rising = (byte)Math.Round(fraction * 255d);
        var falling = (byte)(255 - rising);
        return ((int)Math.Floor(section) % 6) switch
        {
            0 => Color.FromArgb(255, 255, rising, 0),
            1 => Color.FromArgb(255, falling, 255, 0),
            2 => Color.FromArgb(255, 0, 255, rising),
            3 => Color.FromArgb(255, 0, falling, 255),
            4 => Color.FromArgb(255, rising, 0, 255),
            _ => Color.FromArgb(255, 255, 0, falling),
        };
    }
}
