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
    /// 单图竖图最多拉到这么窄的比例（宽/高）。再竖就不是缩略图，是长条了.
    /// </summary>
    private const double MinAspectSingle = 0.68d;

    /// <summary>
    /// 多图时更紧一点：格子一竖，整张动态卡片就跟着长，别让它长成一根棍.
    /// </summary>
    private const double MinAspectMulti = 0.8d;

    /// <summary>
    /// 横图最多压到这么扁的比例（宽/高）.
    /// </summary>
    private const double MaxAspect = 1.6d;

    /// <summary>
    /// 单图高度上限 = 卡片宽 × 这个比例。超了就把图收窄（保比例、不撑满整行），
    /// 免得一张长图把整张动态卡片顶得老高.
    /// </summary>
    private const double SingleImageMaxHeightRatio = 0.55d;

    /// <summary>
    /// 取不到图片尺寸时的兜底比例（正方形）.
    /// </summary>
    private const double AspectFallback = 1d;

    /// <summary>
    /// 解码后才学到真实比例时，偏差超过这个值才重排一次（避免每张图加载都触发一次布局）.
    /// </summary>
    private const double AspectRelearnThreshold = 0.08d;

    /// <summary>
    /// 每批图片最多为「学到真实比例」重排几次，防止解码回调把布局拖进循环.
    /// </summary>
    private const int MaxAspectReflows = 3;

    /// <summary>当前这批图片已经按哪个比例排的版.</summary>
    private double _appliedAspect = AspectFallback;

    /// <summary>为学到比例触发的重排次数.</summary>
    private int _aspectReflowCount;

    /// <summary>已经排过一次版，才允许解码回调去纠正.</summary>
    private bool _hasAppliedLayout;

    /// <summary>重排已经排队，合并掉同一轮里的多次请求.</summary>
    private bool _reflowQueued;

    /// <summary>解码后学到的比例（取已加载图片的中位数）；0 表示还没学到.</summary>
    private double _learnedAspect;

    /// <summary>本轮已经解码完成的图片比例.</summary>
    private readonly List<double> _loadedAspects = new();

    /// <summary>上次记日志时的格子尺寸，用来只在真正变化时落一行.</summary>
    private double _loggedTileWidth = -1d;

    /// <summary>上次记日志时的格子高度.</summary>
    private double _loggedTileHeight = -1d;

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
            // 新的一批图，比例要重新学.
            gallery._appliedAspect = AspectFallback;
            gallery._aspectReflowCount = 0;
            gallery._hasAppliedLayout = false;
            gallery._learnedAspect = 0d;
            gallery._loadedAspects.Clear();
            gallery._loggedTileWidth = -1d;
            gallery._loggedTileHeight = -1d;

            // 宽度此时还是上一轮的值，排到下一轮调度再算，避免用 0 宽算出一列.
            gallery.DispatcherQueue?.TryEnqueue(gallery.UpdateGridMetrics);
        }
    }

    /// <summary>
    /// 缩略图解码完成后拿真实比例。动态列表给来的 BiliImage 没有宽高（Width/Height 是 0），
    /// 只能等解码：ImageExBase 会把 DecodeHeight 按位图比例算出来，两者相除就是原图比例.
    /// </summary>
    private void OnItemImageLoaded(object sender, EventArgs e)
    {
        if (!_hasAppliedLayout || _aspectReflowCount >= MaxAspectReflows)
        {
            return;
        }

        if (sender is not ImageExBase { DecodeWidth: > 0, DecodeHeight: > 0 } image)
        {
            return;
        }

        var total = (ItemsSource as IReadOnlyList<BiliImage>)?.Count ?? 0;
        var isSingle = total == 1;
        _loadedAspects.Add(ClampAspect(image.DecodeWidth / image.DecodeHeight, isSingle));

        // 等这批图都解码完再重排一次：提前重排只会让中位数随解码进度来回跳，
        // 一张卡片连着重排两三次，看着像在抖.
        if (_loadedAspects.Count < total)
        {
            return;
        }

        var median = Median(_loadedAspects);
        if (Math.Abs(median - _appliedAspect) < AspectRelearnThreshold)
        {
            return;
        }

        _learnedAspect = median;
        _aspectReflowCount++;
        QueueReflow();
    }

    private void QueueReflow()
    {
        if (_reflowQueued)
        {
            return;
        }

        _reflowQueued = true;
        DispatcherQueue?.TryEnqueue(() =>
        {
            _reflowQueued = false;
            UpdateGridMetrics();
        });
    }

    /// <summary>
    /// 按可用宽度算列数与格子尺寸，写回 UniformGridLayout.
    /// 列数：1 张一行；2、4 张用 2 列；5~9 张先给 3 列，宽卡片上再往上加（见 <see cref="GetExpandedColumns"/>）；
    /// 格子小于 <see cref="MinTileEdge"/> 就减一列，直到 1 列。
    /// 格子高度：按这批图片实际的长宽比（取中位数）算，让缩略图尽量接近原图比例、
    /// 少裁一点；同一张动态里的格子比例统一。
    /// 单图不强求撑满整行：竖图会把宽度收窄，保证高度不超过卡片宽的
    /// <see cref="SingleImageMaxHeightRatio"/>，免得一张长图把卡片顶得很高。
    /// </summary>
    private void UpdateGridMetrics()
    {
        var width = ActualWidth > 0 ? ActualWidth : ImageRepeater.ActualWidth;
        var images = ItemsSource as IReadOnlyList<BiliImage>;
        var count = images?.Count ?? 0;
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
        var isSingle = count == 1;

        // 学到的真实比例优先（列表接口不给宽高）；还没学到就按模型字段，再没有就按正方形.
        var aspect = _learnedAspect > 0 ? ClampAspect(_learnedAspect, isSingle) : GetRepresentativeAspect(images!, isSingle);
        _appliedAspect = aspect;
        _hasAppliedLayout = true;
        var stretch = isSingle
            ? UniformGridLayoutItemsStretch.None
            : UniformGridLayoutItemsStretch.Fill;

        double tileHeight;
        if (isSingle)
        {
            // 单图：保原图比例。竖图收窄宽度，横图直接用整行宽；高度都有上限.
            var maxHeight = Math.Max(MinTileEdge, width * SingleImageMaxHeightRatio);
            tileWidth = Math.Min(tileWidth, Math.Max(MinTileEdge, maxHeight * aspect));
            tileHeight = tileWidth / aspect;
        }
        else
        {
            tileHeight = tileWidth / aspect;
        }

        // 只在真的变了才写：这几个属性每变一次都会触发一次布局，
        // 反复写会在 SizeChanged 里跟自己打架（并放大 XAML 重入的风险）.
        if (ImageLayout.ItemsStretch != stretch)
        {
            ImageLayout.ItemsStretch = stretch;
        }

        if (!NearlyEquals(ImageLayout.MaximumRowsOrColumns, columns))
        {
            ImageLayout.MaximumRowsOrColumns = columns;
            LogLayout(width, count, aspect, columns, tileWidth, tileHeight);
        }
        else if (!NearlyEquals(_loggedTileWidth, tileWidth) || !NearlyEquals(_loggedTileHeight, tileHeight))
        {
            LogLayout(width, count, aspect, columns, tileWidth, tileHeight);
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
    /// 取这批图片的代表性长宽比（宽/高）：有尺寸的取中位数，避开个别极端值把整行带偏；
    /// 单图直接用它自己的比例。结果夹在 MinAspectSingle/MinAspectMulti 与 MaxAspect 之间，
    /// 免得长图/宽图把卡片撑变形。
    /// </summary>
    private static double GetRepresentativeAspect(IReadOnlyList<BiliImage> images, bool isSingle)
    {
        if (isSingle)
        {
            return ClampAspect(GetAspect(images[0]), true);
        }

        var aspects = new List<double>(images.Count);
        foreach (var image in images)
        {
            var aspect = GetAspect(image);
            if (aspect > 0)
            {
                aspects.Add(aspect);
            }
        }

        if (aspects.Count == 0)
        {
            return AspectFallback;
        }

        aspects.Sort();
        return ClampAspect(aspects[aspects.Count / 2], false);
    }

    private void LogLayout(double width, int count, double aspect, int columns, double tileWidth, double tileHeight)
    {
        _loggedTileWidth = tileWidth;
        _loggedTileHeight = tileHeight;
        Serilog.Log.Information(
            "动态九宫格：可用宽 {Width:F0}，图 {Count} 张（比例 {Aspect:F2}）→ {Columns} 列，格子 {TileW:F0}x{TileH:F0}",
            width,
            count,
            aspect,
            columns,
            tileWidth,
            tileHeight);
    }

    /// <summary>取中位数：个别长图/宽图不至于把整行带偏.</summary>
    private static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static double GetAspect(BiliImage? image)
        => image is { Width: > 0, Height: > 0 } ? image.Width / image.Height : AspectFallback;

    private static double ClampAspect(double aspect, bool isSingle)
        => Math.Clamp(aspect, isSingle ? MinAspectSingle : MinAspectMulti, MaxAspect);

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
