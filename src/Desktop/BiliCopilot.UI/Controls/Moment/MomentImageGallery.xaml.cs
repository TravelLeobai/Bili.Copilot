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
    /// 加列的门槛：格子还得有这个宽度才值得多铺一列，否则只是把图挤小.
    /// </summary>
    private const double ComfortableTileEdge = 110d;

    /// <summary>
    /// 加列的上限：再多列一行就塞不下可看的图，纯属挤.
    /// </summary>
    private const int MaxExpandedColumns = 5;

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
    /// 规则：1 张铺满整行（16:9）；2、4 张用 2 列；5~9 张先给 3 列；
    /// 宽卡片（格子还能保持 <see cref="ComfortableTileEdge"/> 以上）时，
    /// 5 张以上的动态会往上加列 —— 但只接受能整除图片数的列数，
    /// 否则末行会剩孤零零一两个，比三列还难看.
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

        // 卡片够宽时，5 张以上多铺几列（4 张固定 2×2，1/2 张不动）.
        if (count > 4)
        {
            columns = GetExpandedColumns(width, count, columns);
        }

        while (columns > 1 && TileWidthFor(width, columns) < MinTileEdge)
        {
            columns--;
        }

        var tileWidth = TileWidthFor(width, columns);
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

    /// <summary>
    /// 宽卡片上给 5 张以上的动态多铺几列。
    /// 候选列数从 4 往上试到 <see cref="MaxExpandedColumns"/>，同时要求：
    /// 格子不小于 <see cref="ComfortableTileEdge"/>；末行不能只剩一两个（至少占半行）。
    /// 能整除图片数的列数优先 —— 8 张 → 4 列（2×2）比 8 张 → 5 列（5+3）好看.
    /// </summary>
    private static int GetExpandedColumns(double width, int count, int current)
    {
        var best = current;
        var upper = Math.Min(count, MaxExpandedColumns);
        for (var candidate = current + 1; candidate <= upper; candidate++)
        {
            if (TileWidthFor(width, candidate) < ComfortableTileEdge)
            {
                break;
            }

            var rows = (count + candidate - 1) / candidate;
            var lastRow = count - (candidate * (rows - 1));
            var isEven = count % candidate == 0;
            if (isEven || lastRow >= (candidate + 1) / 2)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static double TileWidthFor(double width, int columns)
        => (width - (TileSpacing * (columns - 1))) / columns;

    private static bool NearlyEquals(double a, double b) => Math.Abs(a - b) < 0.5d;

    private void OnImageTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        var image = (sender as FrameworkElement).Tag as BiliImage;
        var itemsSource = ItemsSource as List<BiliImage>;
        new GalleryWindow(image, itemsSource).Activate();
    }
}
