// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Data;

namespace BiliCopilot.UI.Converters;

/// <summary>
/// 把文本截到指定长度，超出补省略号.
/// 长度从 ConverterParameter 读，读不到就用 MaxLength.
/// </summary>
internal sealed partial class TruncateTextConverter : IValueConverter
{
    /// <summary>
    /// 默认最大长度.
    /// </summary>
    public int MaxLength { get; set; } = 12;

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string text || string.IsNullOrEmpty(text))
        {
            return value;
        }

        var max = MaxLength;
        if (parameter is string raw && int.TryParse(raw, out var parsed) && parsed > 0)
        {
            max = parsed;
        }

        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}