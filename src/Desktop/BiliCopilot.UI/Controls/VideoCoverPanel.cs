// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Controls;

/// <summary>
/// 在测量时确定封面比例，避免 SizeChanged 回写高度造成二次布局.
/// </summary>
public sealed partial class VideoCoverPanel : Panel
{
    /// <summary>
    /// 是否保持 16:9 封面比例.
    /// </summary>
    public static readonly DependencyProperty PreserveAspectRatioProperty = DependencyProperty.Register(
        nameof(PreserveAspectRatio), typeof(bool), typeof(VideoCoverPanel),
        new PropertyMetadata(false, OnAspectRatioChanged));

    /// <summary>
    /// 是否保持 16:9 封面比例.
    /// </summary>
    public bool PreserveAspectRatio
    {
        get => (bool)GetValue(PreserveAspectRatioProperty);
        set => SetValue(PreserveAspectRatioProperty, value);
    }

    /// <inheritdoc/>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var constrained = PreserveAspectRatio && double.IsFinite(availableSize.Width);
        var size = constrained
            ? new Windows.Foundation.Size(availableSize.Width, availableSize.Width * 9 / 16)
            : availableSize;
        var desired = new Windows.Foundation.Size();
        foreach (var child in Children)
        {
            child.Measure(size);
            desired.Width = Math.Max(desired.Width, child.DesiredSize.Width);
            desired.Height = Math.Max(desired.Height, child.DesiredSize.Height);
        }

        return constrained ? size : desired;
    }

    /// <inheritdoc/>
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        var bounds = new Windows.Foundation.Rect(0, 0, finalSize.Width, finalSize.Height);
        foreach (var child in Children)
        {
            child.Arrange(bounds);
        }

        return finalSize;
    }

    private static void OnAspectRatioChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((VideoCoverPanel)sender).InvalidateMeasure();
}
