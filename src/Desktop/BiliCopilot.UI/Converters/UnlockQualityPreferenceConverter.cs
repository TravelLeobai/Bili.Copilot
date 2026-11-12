// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using Microsoft.UI.Xaml.Data;

namespace BiliCopilot.UI.Converters;

/// <summary>
/// 画质偏好枚举 → 可读文案.
/// </summary>
internal sealed partial class UnlockQualityPreferenceConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is UnlockQualityPreference preference
            ? preference switch
            {
                UnlockQualityPreference.FullHd60 => "1080P 60 帧优先（无 60 帧时退高码率）",
                _ => "自动选择最高画质",
            }
            : string.Empty;

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
