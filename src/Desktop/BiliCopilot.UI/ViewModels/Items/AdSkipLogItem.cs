// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 播放界面左侧的跳过日志条目，出现后两秒自动消失.
/// </summary>
public sealed class AdSkipLogItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AdSkipLogItem"/> class.
    /// </summary>
    /// <param name="text">日志文本.</param>
    public AdSkipLogItem(string text) => Text = text;

    /// <summary>
    /// 日志文本.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// 创建时间，用来算两秒存活.
    /// </summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
}
