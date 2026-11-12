// Copyright (c) Bili Copilot. All rights reserved.

// 移植自 https://github.com/SuGar0218/TryLiquidGlass-WinUI3，
// 用 Win2D 的位移贴图做出边缘折射的液态玻璃。
//
// 与原版的两处差别：
// 1. 原版必须绑 BackgroundSource（抓它背后元素的渲染结果）。播放器画面在 mpv 的子 HWND 上，
//    XAML 抓不到那块像素，所以没给 BackgroundSource 时用自带的暗色渐变顶替——效果就是暗色玻璃。
// 2. 所有 Win2D 资源都在 Loaded/SizeChanged 里建好，Draw 里只画不建。
//    原版在 Draw 里 new CanvasRenderTarget，多个实例一起画时会踩到
//    DCOMPOSITION_ERROR_SURFACE_BEING_RENDERED 把应用打挂。

using BiliCopilot.UI.Helpers.Glass;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace BiliCopilot.UI.Controls.Glass;

/// <summary>
/// 圆形/圆角矩形液态玻璃底：边缘按位移贴图折射，内部做一档暗色染色.
/// </summary>
[TemplatePart(Name = nameof(CanvasPart), Type = typeof(CanvasControl))]
public sealed partial class LiquidGlassBackground : Control, IDisposable
{
    private const string CanvasPart = "PART_CanvasControl";

    private CanvasControl? _canvas;
    private CanvasRenderTarget? _displacementMap;
    private CanvasBitmap? _backdrop;
    private CanvasGeometry? _clipGeometry;
    private GaussianBlurEffect? _blurEffect;
    private DisplacementMapEffect? _displacementEffect;
    private CropEffect? _cropEffect;
    private FrameworkElementCanvasBitmapSource? _backdropSource;
    private double _builtWidth;
    private double _builtHeight;
    private bool _isBuilding;
    private bool _disposedValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiquidGlassBackground"/> class.
    /// </summary>
    public LiquidGlassBackground()
    {
        DefaultStyleKey = typeof(LiquidGlassBackground);
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// 边缘折射的厚度（像素）.
    /// </summary>
    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>
    /// <see cref="Thickness"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness),
        typeof(double),
        typeof(LiquidGlassBackground),
        new PropertyMetadata(9d, OnVisualPropertyChanged));

    /// <summary>
    /// 圆角半径；等于半宽时就是圆形.
    /// </summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>
    /// <see cref="Radius"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius),
        typeof(double),
        typeof(LiquidGlassBackground),
        new PropertyMetadata(20d, OnVisualPropertyChanged));

    /// <summary>
    /// 暗色染色强度（0~1）.
    /// </summary>
    public double TintOpacity
    {
        get => (double)GetValue(TintOpacityProperty);
        set => SetValue(TintOpacityProperty, value);
    }

    /// <summary>
    /// <see cref="TintOpacity"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty TintOpacityProperty = DependencyProperty.Register(
        nameof(TintOpacity),
        typeof(double),
        typeof(LiquidGlassBackground),
        new PropertyMetadata(0.46d, OnVisualPropertyChanged));

    /// <summary>
    /// 可选的背景来源；给了就抓它的渲染结果做折射，没给就用自带的暗色渐变.
    /// </summary>
    public UIElement? BackgroundSource
    {
        get => (UIElement?)GetValue(BackgroundSourceProperty);
        set => SetValue(BackgroundSourceProperty, value);
    }

    /// <summary>
    /// <see cref="BackgroundSource"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty BackgroundSourceProperty = DependencyProperty.Register(
        nameof(BackgroundSource),
        typeof(UIElement),
        typeof(LiquidGlassBackground),
        new PropertyMetadata(null, OnBackgroundSourceChanged));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposedValue)
        {
            return;
        }

        _backdropSource?.Dispose();
        _displacementMap?.Dispose();
        _backdrop?.Dispose();
        _clipGeometry?.Dispose();
        _blurEffect?.Dispose();
        _displacementEffect?.Dispose();
        _cropEffect?.Dispose();
        _canvas = null;
        _disposedValue = true;
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _canvas = GetTemplateChild(CanvasPart) as CanvasControl;
        if (_canvas is not null)
        {
            _canvas.Draw += OnCanvasDraw;
        }
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as LiquidGlassBackground)?.RequestRebuild();

    private static void OnBackgroundSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not LiquidGlassBackground glass)
        {
            return;
        }

        glass._backdropSource?.Dispose();
        glass._backdropSource = null;
        if (e.NewValue is FrameworkElement element)
        {
            glass._backdropSource = FrameworkElementCanvasBitmapSourceManager.GetBitmapSourceFor(element);
        }

        glass.RequestRebuild();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RequestRebuild();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RequestRebuild();

    /// <summary>
    /// 标记资源需要重建，并尽快在非绘制时机重建一次.
    /// </summary>
    private void RequestRebuild()
    {
        _builtWidth = 0;
        DispatcherQueue?.TryEnqueue(() =>
        {
            BuildResources();
            _canvas?.Invalidate();
        });
    }

    private void BuildResources()
    {
        if (_isBuilding || _canvas is null)
        {
            return;
        }

        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _isBuilding = true;
        try
        {
            _blurEffect ??= new GaussianBlurEffect { BlurAmount = 1.6f, BorderMode = EffectBorderMode.Hard };
            _displacementEffect ??= new DisplacementMapEffect
            {
                XChannelSelect = EffectChannelSelect.Red,
                YChannelSelect = EffectChannelSelect.Green,
            };
            _cropEffect ??= new CropEffect();

            _displacementMap?.Dispose();
            _displacementMap = BuildDisplacementMap(_canvas, width, height);

            _backdrop?.Dispose();
            _backdrop = BuildBackdrop(_canvas, width, height);

            _clipGeometry?.Dispose();
            var radius = (float)Math.Min(Radius, Math.Min(width, height) / 2);
            _clipGeometry = CanvasGeometry.CreateRoundedRectangle(
                _canvas,
                new Rect(0, 0, width, height),
                radius,
                radius);

            _builtWidth = width;
            _builtHeight = height;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "液态玻璃资源构建失败");
        }
        finally
        {
            _isBuilding = false;
        }
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_displacementMap is null || _backdrop is null || _clipGeometry is null
            || Math.Abs(_builtWidth - width) > 0.5 || Math.Abs(_builtHeight - height) > 0.5)
        {
            RequestRebuild();
            return;
        }

        var session = args.DrawingSession;
        var radius = (float)Math.Min(Radius, Math.Min(width, height) / 2);

        using (session.CreateLayer(1f, _clipGeometry))
        {
            if (_blurEffect is not null && _displacementEffect is not null && _cropEffect is not null)
            {
                _cropEffect.Source = _backdrop;
                _cropEffect.SourceRectangle = new Rect(0, 0, width, height);
                _displacementEffect.Source = _cropEffect;
                _displacementEffect.Displacement = _displacementMap;
                _displacementEffect.Amount = Math.Min(width, height);
                _blurEffect.Source = _displacementEffect;
                session.DrawImage(_blurEffect);
            }
            else
            {
                session.DrawImage(_backdrop, new Rect(0, 0, width, height));
            }

            session.FillRectangle(
                0,
                0,
                width,
                height,
                Color.FromArgb((byte)(Math.Clamp(TintOpacity, 0, 1) * 255), 0x0C, 0x0E, 0x12));

            session.DrawRoundedRectangle(
                0.5f,
                0.5f,
                width - 1,
                height - 1,
                radius,
                radius,
                Color.FromArgb(64, 255, 255, 255),
                1f);
        }
    }

    private CanvasRenderTarget BuildDisplacementMap(CanvasControl canvas, double width, double height)
    {
        var renderTarget = new CanvasRenderTarget(canvas, (float)width, (float)height, canvas.Dpi);
        var scale = canvas.Dpi / 96d;

        // 这里必须用 SizeInPixels：构造函数收的是 DIP，内部会按 DPI 换算成像素，
        // SetPixelColors 要求的正是像素数（自己再乘一遍 DPI 会得到 size 不匹配的异常）.
        var pixelWidth = Math.Max(1, (int)renderTarget.SizeInPixels.Width);
        var pixelHeight = Math.Max(1, (int)renderTarget.SizeInPixels.Height);

        var thickness = Math.Max(1, (int)(Thickness * scale));
        var radius = (float)Math.Min(Radius, Math.Min(width, height) / 2) * (float)scale;
        var maxDisplacement = Math.Min(pixelWidth, pixelHeight);
        var distance = new DisplacementDistanceFunction(thickness, maxDisplacement);
        var field = new RoundedCornerRectangleDisplacementFunction(distance, pixelWidth, pixelHeight, radius);

        var pixels = new Color[pixelWidth * pixelHeight];
        for (var y = 0; y < pixelHeight; y++)
        {
            for (var x = 0; x < pixelWidth; x++)
            {
                var (offsetX, offsetY) = field.Calculate(x, y);
                pixels[(y * pixelWidth) + x] = Color.FromArgb(
                    255,
                    (byte)(128 + (255 * offsetX / maxDisplacement / 2)),
                    (byte)(128 + (255 * offsetY / maxDisplacement / 2)),
                    128);
            }
        }

        renderTarget.SetPixelColors(pixels);
        return renderTarget;
    }

    private CanvasBitmap BuildBackdrop(CanvasControl canvas, double width, double height)
    {
        var renderTarget = new CanvasRenderTarget(canvas, (float)width, (float)height, canvas.Dpi);
        using (var session = renderTarget.CreateDrawingSession())
        {
            var gradient = new CanvasLinearGradientBrush(
                canvas,
                Color.FromArgb(255, 0x3A, 0x3F, 0x48),
                Color.FromArgb(255, 0x10, 0x12, 0x16))
            {
                StartPoint = new System.Numerics.Vector2(0, 0),
                EndPoint = new System.Numerics.Vector2(0, (float)height),
            };
            session.FillRectangle(0, 0, (float)width, (float)height, gradient);
        }

        return renderTarget;
    }
}


