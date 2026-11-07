// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 播放界面左侧的跳过日志条目，出现一段时间后自动消失.
/// </summary>
public sealed class AdSkipLogItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AdSkipLogItem"/> class.
    /// </summary>
    /// <param name="text">日志文本.</param>
    /// <param name="life">存活时间，默认两秒.</param>
    public AdSkipLogItem(string text, TimeSpan? life = null)
    {
        Text = text;
        Life = life ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// 日志文本.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// 存活时间：跳片段那种一闪而过的用两秒，检查过程的分步日志留久一点才看得完.
    /// </summary>
    public TimeSpan Life { get; }

    /// <summary>
    /// 创建时间，用来算存活.
    /// </summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
}
