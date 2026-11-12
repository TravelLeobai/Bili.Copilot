// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;

namespace BiliCopilot.UI.Helpers.Glass;

/// <summary>
/// 同一个元素只建一份 <see cref="FrameworkElementCanvasBitmapSource"/>，避免重复抓图.
/// </summary>
internal static class FrameworkElementCanvasBitmapSourceManager
{
    private static readonly ConditionalWeakTable<FrameworkElement, FrameworkElementCanvasBitmapSource> Sources = [];

    /// <summary>
    /// 取（或创建）该元素对应的位图来源.
    /// </summary>
    /// <param name="target">目标元素.</param>
    /// <returns>位图来源.</returns>
    public static FrameworkElementCanvasBitmapSource GetBitmapSourceFor(FrameworkElement target)
    {
        if (!Sources.TryGetValue(target, out var bitmapSource))
        {
            bitmapSource = new FrameworkElementCanvasBitmapSource(target);
            Sources.Add(target, bitmapSource);
        }

        return bitmapSource;
    }
}
