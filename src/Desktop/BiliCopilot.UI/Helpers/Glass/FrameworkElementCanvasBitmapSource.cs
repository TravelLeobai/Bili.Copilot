// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.DirectX;

namespace BiliCopilot.UI.Helpers.Glass;

/// <summary>
/// 把一个 <see cref="FrameworkElement"/> 的当前渲染结果抓成 <see cref="CanvasBitmap"/>，
/// 供液态玻璃做背景折射用.
/// </summary>
internal sealed partial class FrameworkElementCanvasBitmapSource : IDisposable
{
    private readonly FrameworkElement _target;
    private readonly RenderTargetBitmapSource _renderTargetBitmapSource;
    private bool _disposedValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrameworkElementCanvasBitmapSource"/> class.
    /// </summary>
    /// <param name="target">要抓取的元素.</param>
    public FrameworkElementCanvasBitmapSource(FrameworkElement target)
    {
        _target = target;
        _target.SizeChanged += OnSizeChanged;
        _renderTargetBitmapSource = new RenderTargetBitmapSource(target);
    }

    /// <summary>
    /// 背景失效（尺寸变化等）时触发，调用方据此重画.
    /// </summary>
    public event EventHandler? Invalidated;

    /// <summary>
    /// 标记背景已过期，下次取用时重抓.
    /// </summary>
    public void Invalidate()
    {
        _renderTargetBitmapSource.Invalidate();
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 取当前元素的位图.
    /// </summary>
    /// <param name="canvasResourceCreator">Win2D 资源创建器.</param>
    /// <returns>位图.</returns>
    public async Task<CanvasBitmap> GetAsync(ICanvasResourceCreator canvasResourceCreator)
    {
        var renderTargetBitmap = await _renderTargetBitmapSource.GetAsync();
        var pixelBuffer = await renderTargetBitmap.GetPixelsAsync();
        return CanvasBitmap.CreateFromBytes(
            canvasResourceCreator,
            pixelBuffer.ToBytes(),
            renderTargetBitmap.PixelWidth,
            renderTargetBitmap.PixelHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            (float)(_target.XamlRoot.RasterizationScale * 96));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposedValue)
        {
            return;
        }

        _target.SizeChanged -= OnSizeChanged;
        _disposedValue = true;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Invalidate();
}
