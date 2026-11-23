// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BiliCopilot.UI.Toolkits;

/// <summary>
/// 投票饼图的配色。固定的一套颜色，深浅主题都能看清，不跟强调色打架.
/// </summary>
internal static class CommentVotePalette
{
    private static readonly Color[] Colors =
    [
        ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6), // 蓝
        ColorHelper.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), // 琥珀
        ColorHelper.FromArgb(0xFF, 0x10, 0xB9, 0x81), // 绿
        ColorHelper.FromArgb(0xFF, 0x8B, 0x5C, 0xF6), // 紫
        ColorHelper.FromArgb(0xFF, 0xEF, 0x44, 0x44), // 红
        ColorHelper.FromArgb(0xFF, 0x06, 0xB6, 0xD4), // 青
    ];

    private static readonly SolidColorBrush[] Brushes = Colors.Select(color => new SolidColorBrush(color)).ToArray();

    /// <summary>
    /// 取第 n 项的颜色（循环使用）.
    /// </summary>
    /// <param name="order">选项在列表里的位置（0 起）.</param>
    /// <returns>画刷.</returns>
    public static SolidColorBrush GetBrush(int order)
    {
        var index = order % Brushes.Length;
        return index < 0 ? Brushes[0] : Brushes[index];
    }
}
