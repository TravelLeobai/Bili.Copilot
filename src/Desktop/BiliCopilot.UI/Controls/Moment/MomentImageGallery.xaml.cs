// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Forms;
using Richasy.BiliKernel.Models.Appearance;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 动态图片画廊.
/// </summary>
public sealed partial class MomentImageGallery : LayoutUserControlBase
{
    /// <summary>
    /// 格子之间的间距，和 XAML 里的 MinColumnSpacing / MinRowSpacing 保持一致.
    /// </summary>
    private const double TileSpacing = 4d;

    /// <summary>
    /// 再小就不如减一列：低于这个宽度的格子看不清内容，还会把图片裁得只剩中间一条.
    /// </summary>
    private const double MinTileEdge = 84d;

    /// <summary>
    /// 单图时的目标宽高比（16:9）。B 站动态里单图通常是宽幅，缩成方图会把上下裁掉.
    /// </summary>
    private const double SingleImageAspect = 9d / 16d;

    /// <summary>
    /// <see cref="ItemsSource"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(object),
            typeof(MomentImageGallery),
            new PropertyMetadata(default, OnItemsSourceChanged));

    /// <summary>
    /// Initializes a new instance of the <see cref="MomentImageGallery"/> class.
    /// </summary>
    public MomentImageGallery()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateGridMetrics();
        Loaded += (_, _) => UpdateGridMetrics();
    }

    /// <summary>
    /// 图片源.
    /// </summary>
    public object ItemsSource
    {
        get => (object)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnControlUnloaded()
        => ImageRepeater.ItemsSource = default;

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MomentImageGallery gallery)
        {
            // 宽度此时还是上一轮的值，排到下一轮调度再算，避免用 0 宽算出一列.
            gallery.DispatcherQueue?.TryEnqueue(gallery.UpdateGridMetrics);
        }
    }

    /// <summary>
    /// 按可用宽度算列数与格子尺寸，写回 UniformGridLayout.
    /// 规则：1 张铺满整行（16:9）；2、4 张用 2 列；其余最多 3 列；
    /// 格子小于 <see cref="MinTileEdge"/> 就减一列，直到 1 列.
    /// </summary>
    private void UpdateGridMetrics()
    {
        var width = ActualWidth > 0 ? ActualWidth : ImageRepeater.ActualWidth;
        var count = (ItemsSource as IReadOnlyList<BiliImage>)?.Count ?? 0;
        if (width <= 0 || count == 0)
        {
            return;
        }

        var columns = count switch
        {
            1 => 1,
            2 or 4 => 2,
            _ => 3,
        };

        while (columns > 1 && ((width - (TileSpacing * (columns - 1))) / columns) < MinTileEdge)
        {
            columns--;
        }

        var tileWidth = (width - (TileSpacing * (columns - 1))) / columns;
        var tileHeight = count == 1 ? Math.Max(MinTileEdge, width * SingleImageAspect) : tileWidth;

        // 只在真的变了才写：这几个属性每变一次都会触发一次布局，
        // 反复写会在 SizeChanged 里跟自己打架（并放大 XAML 重入的风险）.
        if (!NearlyEquals(ImageLayout.MaximumRowsOrColumns, columns))
        {
            ImageLayout.MaximumRowsOrColumns = columns;
            Serilog.Log.Information(
                "动态九宫格：可用宽 {Width:F0}，图 {Count} 张 → {Columns} 列，格子 {Tile:F0}",
                width,
                count,
                columns,
                tileWidth);
        }

        if (!NearlyEquals(ImageLayout.MinItemWidth, tileWidth))
        {
            ImageLayout.MinItemWidth = tileWidth;
        }

        if (!NearlyEquals(ImageLayout.MinItemHeight, tileHeight))
        {
            ImageLayout.MinItemHeight = tileHeight;
        }
    }

    private static bool NearlyEquals(double a, double b) => Math.Abs(a - b) < 0.5d;

    private void OnImageTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        var image = (sender as FrameworkElement).Tag as BiliImage;
        var itemsSource = ItemsSource as List<BiliImage>;
        new GalleryWindow(image, itemsSource).Activate();
    }
}
