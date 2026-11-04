// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls;

public sealed partial class BasicCoverImage : ImageExBase
{
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
