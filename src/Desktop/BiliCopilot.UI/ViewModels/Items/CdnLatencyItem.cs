// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// CDN 节点延迟测试的一行结果：主机名 + 延迟 + 按延迟分档的颜色.
/// 颜色分档只影响观感，不参与优选决策（优选看的是真实 Mbps）.
/// </summary>
public sealed class CdnLatencyItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CdnLatencyItem"/> class.
    /// </summary>
    public CdnLatencyItem(string host, int delayMs)
    {
        Host = host;
        DelayMs = delayMs;
        DelayText = delayMs < 0 ? "不可用" : $"{delayMs} ms";
        DelayBrush = new SolidColorBrush(ResolveColor(delayMs));
    }

    /// <summary>
    /// 节点主机名.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// 延迟（毫秒），-1 表示不可用.
    /// </summary>
    public int DelayMs { get; }

    /// <summary>
    /// 显示用的延迟文本.
    /// </summary>
    public string DelayText { get; }

    /// <summary>
    /// 延迟颜色.
    /// </summary>
    public Brush DelayBrush { get; }

    private static Windows.UI.Color ResolveColor(int delayMs)
        => delayMs < 0
            ? ColorHelper.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)
            : delayMs < 50
                ? ColorHelper.FromArgb(0xFF, 0x4C, 0xC3, 0x8A)
                : delayMs < 100
                    ? ColorHelper.FromArgb(0xFF, 0x8B, 0xC3, 0x4A)
                    : delayMs < 200
                        ? ColorHelper.FromArgb(0xFF, 0xE5, 0xA5, 0x4B)
                        : ColorHelper.FromArgb(0xFF, 0xD9, 0x53, 0x4F);
}
