// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls;

public sealed partial class BasicCoverImage : ImageExBase
{
    private readonly static BasicScaleEffect _scaleEffect = new();

    private bool _isNormalizingSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasicCoverImage"/> class.
    /// </summary>
    public BasicCoverImage()
    {
        // Source 或解码宽度变化时把地址补成服务端缩略图：列表只下缩略图.
        RegisterPropertyChangedCallback(SourceProperty, (_, _) => NormalizeSource());
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
    /// 把 Source 换成带服务端缩略参数的地址；不支持或已在缩略图上时保持原样.
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
        if (thumbnail is not Uri thumbUri || Equals(thumbUri, current))
        {
            return;
        }

        _isNormalizingSource = true;
        try
        {
            Source = thumbUri;
        }
        finally
        {
            _isNormalizingSource = false;
        }
    }
}
