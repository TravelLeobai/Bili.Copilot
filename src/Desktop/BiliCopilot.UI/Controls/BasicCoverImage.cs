// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls;

public sealed partial class BasicCoverImage : ImageExBase
{
    // 解码尺寸档位：同档位的图共用一份磁盘缓存和解码结果，
    // 档位不散就不会出现「一张图七八种尺寸」的重复下载。
    private static readonly int[] DecodeBuckets = [32, 48, 64, 96, 128, 192, 256, 320, 384, 480, 600];

    private readonly static BasicScaleEffect _scaleEffect = new();

    private readonly System.Diagnostics.Stopwatch _loadStopwatch = new();

    private bool _isNormalizingSource;

    // XAML 里写的 DecodeWidth 现在当作「上限」：真实解码宽度按控件实际渲染宽度 × 缩放算，
    // 但不超过这个上限。这样 600 的封面卡在小列里不会白解一张 600 宽的图，
    // 高 DPI 下 100 的头像也不会因为不够而糊。
    private double _maxDecodeWidth;

    // 只在「尺寸明显变化」时才换解码宽度，否则每次布局微调都会丢掉已有解码结果、重新请求.
    private const double DecodeChangeThreshold = 0.25;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasicCoverImage"/> class.
    /// </summary>
    public BasicCoverImage()
    {
        _ = _loadStopwatch;

        // 诊断开关打开时，解码完成/失败都记一笔，用来判断失败重试风暴和解码开销.
        ImageLoaded += (_, _) => ImageDiagnostics.RecordLoaded(
            Source?.OriginalString,
            (int)DecodeWidth,
            (int)DecodeHeight,
            _loadStopwatch.Elapsed.TotalMilliseconds);

        ImageFailed += (_, _) => ImageDiagnostics.RecordFailed(Source?.OriginalString, null);

        // Source 或解码宽度变化时把地址补成服务端缩略图：列表只下缩略图.
        RegisterPropertyChangedCallback(SourceProperty, (_, _) =>
        {
            _loadStopwatch.Restart();
            NormalizeSource();
        });

        RegisterPropertyChangedCallback(DecodeWidthProperty, (_, _) => NormalizeSource());

        // 布局给了真实尺寸之后再决定解码宽度：XAML 里那个值只作为上限.
        SizeChanged += (_, e) => ApplyAutoDecodeWidth(e.NewSize.Width);

        // 缩略图地址是按「逻辑宽度」拼的，窗口缩放/DPI 变化后要重算一次.
        Loaded += (_, _) => ApplyAutoDecodeWidth(ActualWidth);
    }

    protected override string GetCacheSubFolder()
        => "ImageCache";

    protected override HttpClient? GetCustomHttpClient()
        => InternalHttpExtensions.ImageClient;

    /// <inheritdoc/>
    protected override void DrawImage(CanvasBitmap canvasBitmap)
    {
        var width = canvasBitmap.Size.Width;
        var height = canvasBitmap.Size.Height;
        var aspectRatio = width / height;
        var actualHeight = DecodeWidth / aspectRatio;
        if (Math.Abs(DecodeHeight - actualHeight) > 1)
        {
            DecodeHeight = Math.Round(actualHeight);
            CanvasImageSource = new Microsoft.Graphics.Canvas.UI.Xaml.CanvasImageSource(
                resourceCreator: CanvasDevice.GetSharedDevice(),
                width: (float)DecodeWidth,
                height: (float)DecodeHeight,
                dpi: 96,
                CanvasAlphaMode.Premultiplied);
        }

        var destRect = new Rect(0, 0, DecodeWidth, DecodeHeight);
        var sourceRect = new Rect(0, 0, width, height);
        DrawBlurImage(canvasBitmap, destRect, sourceRect);
    }

    /// <summary>
    /// 按控件实际渲染尺寸（含 DPI 缩放）决定解码宽度，并吸附到档位。
    /// </summary>
    /// <param name="renderedWidth">控件渲染宽度（逻辑像素）.</param>
    private void ApplyAutoDecodeWidth(double renderedWidth)
    {
        if (renderedWidth <= 1)
        {
            return;
        }

        // XAML 里没有显式给值时，_maxDecodeWidth 是 0：用当前默认当上限.
        if (_maxDecodeWidth <= 0)
        {
            _maxDecodeWidth = DecodeWidth > 0 ? DecodeWidth : 600;
        }

        var scale = XamlRoot?.RasterizationScale ?? 1d;
        var needed = renderedWidth * scale;

        // 不放大到上限以上：上限代表这块图最多需要多少像素.
        var target = Math.Min(needed, _maxDecodeWidth);

        // 预估误差允许 25%：低于这个差值就不动，避免反复重新解码.
        var bucket = PickBucket(target);
        var current = DecodeWidth;
        if (current > 0 && Math.Abs(bucket - current) <= current * DecodeChangeThreshold)
        {
            return;
        }

        DecodeWidth = bucket;
    }

    private static double PickBucket(double target)
    {
        foreach (var bucket in DecodeBuckets)
        {
            if (bucket >= target)
            {
                return bucket;
            }
        }

        return DecodeBuckets[^1];
    }

    private void DrawBlurImage(
        CanvasBitmap canvasBitmap,
        Rect destinationRect,
        Rect sourceRect)
    {
        if (destinationRect.Width <= 0 || destinationRect.Height <= 0)
        {
            return;
        }

        _scaleEffect.Source = canvasBitmap;
        _scaleEffect.SourceRectangle = sourceRect;
        _scaleEffect.DestinationRectangle = destinationRect;
        using var ds = CanvasImageSource!.CreateDrawingSession(Colors.Transparent);
        ds.DrawImage(_scaleEffect, destinationRect, destinationRect);
    }

    /// <summary>
    /// 把 Source 换成带服务端缩略参数的地址；不支持或已有参数时保持原样.
    /// </summary>
    private void NormalizeSource()
    {
        if (_isNormalizingSource)
        {
            return;
        }

        if (_maxDecodeWidth <= 0 && DecodeWidth > 0)
        {
            _maxDecodeWidth = DecodeWidth;
        }

        var current = Source;
        if (current is null || DecodeWidth <= 0)
        {
            return;
        }

        var thumbnail = ImageUrlToolkit.BuildThumbnailSource(current, DecodeWidth);
        if (thumbnail is null || Equals(thumbnail, current))
        {
            return;
        }

        _isNormalizingSource = true;
        try
        {
            SetValue(SourceProperty, thumbnail);
        }
        finally
        {
            _isNormalizingSource = false;
        }
    }
}
