// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls;

public sealed partial class BasicCoverImage : ImageExBase
{
    // 解码宽度档位：同档位共用一份磁盘缓存与解码结果，不散成一堆相近尺寸.
    private static readonly int[] DecodeBuckets = [64, 96, 128, 160, 200, 256, 320, 400, 480, 600, 720, 960, 1280];

    private readonly static BasicScaleEffect _scaleEffect = new();

    private readonly System.Diagnostics.Stopwatch _loadStopwatch = new();

    private bool _isNormalizingSource;

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

        // 布局给到真实宽度后再看够不够：只在不足时抬高解码宽度.
        // 只抬不降 —— 往下调会把服务端缩略图钉在很小的尺寸上，放大后就是马赛克.
        SizeChanged += (_, e) => RaiseDecodeWidthIfNeeded(e.NewSize.Width);
        Loaded += (_, _) => RaiseDecodeWidthIfNeeded(ActualWidth);
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
    /// 实际渲染需要的像素 = 渲染宽度 × DPI 缩放；比当前解码宽度大就抬到最近的档位.
    /// 只抬不降：降了会让服务端缩略图变小，之后放大显示就是马赛克.
    /// </summary>
    /// <param name="renderedWidth">控件渲染宽度（逻辑像素）.</param>
    private void RaiseDecodeWidthIfNeeded(double renderedWidth)
    {
        if (renderedWidth <= 1 || DecodeWidth <= 0)
        {
            return;
        }

        var scale = XamlRoot?.RasterizationScale ?? 1d;
        var needed = renderedWidth * scale;
        if (needed <= DecodeWidth + 1)
        {
            return;
        }

        foreach (var bucket in DecodeBuckets)
        {
            if (bucket >= needed)
            {
                if (bucket > DecodeWidth)
                {
                    DecodeWidth = bucket;
                }

                return;
            }
        }
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
