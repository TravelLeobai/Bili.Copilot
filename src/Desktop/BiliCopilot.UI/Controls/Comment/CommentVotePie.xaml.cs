// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Point = Windows.Foundation.Point;
using Size = Windows.Foundation.Size;

namespace BiliCopilot.UI.Controls.Comment;

/// <summary>
/// 评论投票的饼图：一片一项，点一片就是选这一项。
/// <para>
/// 分片角度与点击命中的算法在 <see cref="CommentVoteGeometry"/> 里（纯数学、有单测），
/// 这里只负责画出来和把点击转成 <see cref="CommentVoteViewModel.ToggleOption"/>。
/// </para>
/// <para>
/// 键盘与读屏走右边的图例（选项行），饼图本身只接指针，避免出现两套互相打架的焦点。
/// </para>
/// </summary>
public sealed partial class CommentVotePie : UserControl
{
    /// <summary>
    /// <see cref="ViewModel"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(CommentVoteViewModel), typeof(CommentVotePie), new PropertyMetadata(default(CommentVoteViewModel), OnViewModelChanged));

    private const double OuterRadius = 60d;
    private const double InnerRadius = 33d;
    private const double Center = 62d;

    // 中间空洞的半径：点在这里不算选任何一项（角度没有意义）.
    private const double DeadZoneRadius = 18d;

    private CommentVoteViewModel? _observed;
    private IReadOnlyList<double> _sweeps = Array.Empty<double>();

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVotePie"/> class.
    /// </summary>
    public CommentVotePie()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Gets or sets 要画的投票.
    /// </summary>
    public CommentVoteViewModel? ViewModel
    {
        get => (CommentVoteViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pie = (CommentVotePie)d;
        pie.Observe(pie.IsLoaded ? e.NewValue as CommentVoteViewModel : null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Observe(ViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => Observe(null);

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;

    private static Point PointAt(double radius, double angleFromTop)
    {
        var radians = ToRadians(angleFromTop - 90d);
        return new Point(Center + (radius * Math.Cos(radians)), Center + (radius * Math.Sin(radians)));
    }

    private static FrameworkElement BuildSlice(int order, double start, double sweep, bool isSelected)
    {
        var brush = Toolkits.CommentVotePalette.GetBrush(order);

        // 整圈没有起点终点可分，抠掉一个极小角，画成一个几乎闭合的环.
        var end = start + Math.Min(sweep, 359.9d);
        var figure = new PathFigure
        {
            StartPoint = PointAt(OuterRadius, start),
            IsClosed = true,
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointAt(OuterRadius, end),
            Size = new Size(OuterRadius, OuterRadius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = (end - start) > 180d,
        });
        figure.Segments.Add(new LineSegment { Point = PointAt(InnerRadius, end) });
        figure.Segments.Add(new ArcSegment
        {
            Point = PointAt(InnerRadius, start),
            Size = new Size(InnerRadius, InnerRadius),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = (end - start) > 180d,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new Path
        {
            Data = geometry,
            Fill = brush,
            Opacity = isSelected ? 1d : 0.55d,
            Stroke = brush,
            StrokeThickness = isSelected ? 1.5d : 0.5d,
            IsHitTestVisible = false,
        };
    }

    private void Observe(CommentVoteViewModel? viewModel)
    {
        if (ReferenceEquals(_observed, viewModel))
        {
            Redraw();
            return;
        }

        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
            _observed.Options.CollectionChanged -= OnOptionsChanged;
            foreach (var option in _observed.Options)
            {
                option.PropertyChanged -= OnOptionPropertyChanged;
            }
        }

        _observed = viewModel;

        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
            _observed.Options.CollectionChanged += OnOptionsChanged;
            foreach (var option in _observed.Options)
            {
                option.PropertyChanged += OnOptionPropertyChanged;
            }
        }

        Redraw();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CommentVoteViewModel.Phase)
            or nameof(CommentVoteViewModel.IsChoosing)
            or nameof(CommentVoteViewModel.IsSubmitting))
        {
            Redraw();
        }
    }

    private void OnOptionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 选项被重建成新对象了，重新挂钩.
        if (_observed is not null)
        {
            foreach (var option in _observed.Options)
            {
                option.PropertyChanged -= OnOptionPropertyChanged;
                option.PropertyChanged += OnOptionPropertyChanged;
            }
        }

        Redraw();
    }

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CommentVoteOptionViewModel.Count)
            or nameof(CommentVoteOptionViewModel.IsChecked)
            or nameof(CommentVoteOptionViewModel.IsMine))
        {
            Redraw();
        }
    }

    private void Redraw()
    {
        SliceCanvas.Children.Clear();
        var viewModel = _observed;
        if (viewModel is null || viewModel.Options.Count == 0 || !viewModel.IsReady)
        {
            _sweeps = Array.Empty<double>();
            CenterCount.Text = string.Empty;
            CenterLabel.Visibility = Visibility.Collapsed;
            SliceCanvas.IsHitTestVisible = false;
            return;
        }

        var counts = viewModel.Options.Select(option => option.Count).ToList();
        _sweeps = CommentVoteGeometry.ComputeSweeps(counts);

        var total = counts.Sum(count => Math.Max(0, count));
        CenterCount.Text = total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        CenterLabel.Visibility = Visibility.Visible;
        CenterLabel.Text = total > 0 ? "票" : "还没人投";

        SliceCanvas.IsHitTestVisible = viewModel.IsChoosing && !viewModel.IsSubmitting;

        var start = 0d;
        for (var i = 0; i < viewModel.Options.Count && i < _sweeps.Count; i++)
        {
            var slice = BuildSlice(i, start, _sweeps[i], viewModel.Options[i].IsChecked);
            SliceCanvas.Children.Add(slice);
            start += _sweeps[i];
        }
    }

    private void OnCanvasTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_observed is not { IsChoosing: true, IsSubmitting: false } viewModel || _sweeps.Count == 0)
        {
            return;
        }

        var point = e.GetPosition(SliceCanvas);
        var dx = point.X - Center;
        var dy = point.Y - Center;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance < DeadZoneRadius || distance > OuterRadius + 6d)
        {
            return;
        }

        // 从 12 点方向顺时针算角度.
        var angle = CommentVoteGeometry.Normalize((Math.Atan2(dx, -dy) * 180d / Math.PI) + 360d);
        var index = CommentVoteGeometry.HitTest(_sweeps, angle);
        if (index < 0 || index >= viewModel.Options.Count)
        {
            return;
        }

        viewModel.ToggleOption(viewModel.Options[index]);
        e.Handled = true;
    }
}
