// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BiliCopilot.UI.Helpers.Glass;

/// <summary>
/// 带缓存的 RenderTargetBitmap 包装：元素不变时不重复抓图.
/// </summary>
internal sealed class RenderTargetBitmapSource
{
    private readonly UIElement _target;
    private readonly RenderTargetBitmap _renderTargetBitmap = new();
    private bool _isValid;
    private Task? _renderingTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="RenderTargetBitmapSource"/> class.
    /// </summary>
    /// <param name="target">要抓取的元素.</param>
    public RenderTargetBitmapSource(UIElement target) => _target = target;

    /// <summary>
    /// 缓存失效时触发.
    /// </summary>
    public event EventHandler? Invalidated;

    /// <summary>
    /// 把缓存标记为过期.
    /// </summary>
    public void Invalidate()
    {
        _isValid = false;
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 取当前位图（必要时重新渲染）.
    /// </summary>
    /// <returns>位图.</returns>
    public async Task<RenderTargetBitmap> GetAsync()
    {
        if (!_isValid)
        {
            _renderingTask ??= _renderTargetBitmap.RenderAsync(_target).AsTask();
            await _renderingTask;
            _renderingTask = null;
            _isValid = true;
        }

        return _renderTargetBitmap;
    }
}

