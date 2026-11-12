// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace BiliCopilot.UI.Controls.Glass;

/// <summary>
/// Thickness 取反（模板里用负 margin 抵消边框厚度）.
/// </summary>
internal sealed partial class ThicknessNegativeConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is Thickness thickness
            ? new Thickness(-thickness.Left, -thickness.Top, -thickness.Right, -thickness.Bottom)
            : value;

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Thickness thickness
            ? new Thickness(-thickness.Left, -thickness.Top, -thickness.Right, -thickness.Bottom)
            : value;
}
