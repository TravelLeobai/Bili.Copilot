// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using Microsoft.UI.Xaml.Data;

namespace BiliCopilot.UI.Converters;

internal sealed partial class MomentVideoOpenedMarkStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (MomentVideoOpenedMarkStyle)value switch
        {
            MomentVideoOpenedMarkStyle.None => ResourceToolkit.GetLocalizedString(StringNames.MomentVideoOpenedMarkNone),
            MomentVideoOpenedMarkStyle.AccentBorder => ResourceToolkit.GetLocalizedString(StringNames.MomentVideoOpenedMarkBorder),
            MomentVideoOpenedMarkStyle.Badge => ResourceToolkit.GetLocalizedString(StringNames.MomentVideoOpenedMarkBadge),
            MomentVideoOpenedMarkStyle.Dimmed => ResourceToolkit.GetLocalizedString(StringNames.MomentVideoOpenedMarkDimmed),
            MomentVideoOpenedMarkStyle.RainbowBorder => ResourceToolkit.GetLocalizedString(StringNames.MomentVideoOpenedMarkRainbow),
            _ => string.Empty,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
