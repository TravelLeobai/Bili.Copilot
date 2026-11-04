// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型 - 跳过广告的过程日志.
/// 药丸状小条贴左下角出现，每条自己算两秒，之后消失；新的一条从下面顶上来.
/// </summary>
public sealed partial class PlayerViewModel
{
    private DispatcherQueueTimer? _adSkipLogTimer;

    /// <summary>
    /// 跳过广告的过程中日志（新的在集合末尾，界面上贴着底部往上堆）.
    /// </summary>
    public ObservableCollection<AdSkipLogItem> AdSkipLogs { get; } = new();

    /// <summary>
    /// 时间戳写法：1:23 这种，超过一小时才带小时.
    /// </summary>
    /// <param name="seconds">秒.</param>
    /// <returns>格式化后的时间戳.</returns>
    internal static string FormatSkipStamp(double seconds)
        => seconds >= 3600
            ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss")
            : TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    /// <summary>
    /// 追加一条日志，两秒后自动移除.
    /// </summary>
    /// <param name="text">日志文本.</param>
    internal void PushAdSkipLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        queue.TryEnqueue(() =>
        {
            AdSkipLogs.Add(new AdSkipLogItem(text));
            logger.LogInformation("跳过日志：{Text}（当前 {Count} 条）", text, AdSkipLogs.Count);

            // 正常最多堆 4 条，多出来的直接丢掉，别把左下角铺满.
            while (AdSkipLogs.Count > 4)
            {
                AdSkipLogs.RemoveAt(0);
            }

            EnsureAdSkipLogTimer();
        });
    }

    private void EnsureAdSkipLogTimer()
    {
        if (_adSkipLogTimer is null)
        {
            _adSkipLogTimer = queue.CreateTimer();
            _adSkipLogTimer.Interval = TimeSpan.FromMilliseconds(120);
            _adSkipLogTimer.Tick += (_, _) =>
            {
                var now = DateTimeOffset.Now;
                for (var i = AdSkipLogs.Count - 1; i >= 0; i--)
                {
                    if (now - AdSkipLogs[i].CreatedAt >= TimeSpan.FromSeconds(2))
                    {
                        AdSkipLogs.RemoveAt(i);
                    }
                }

                if (AdSkipLogs.Count == 0)
                {
                    _adSkipLogTimer?.Stop();
                }
            };
        }

        if (!_adSkipLogTimer.IsRunning)
        {
            _adSkipLogTimer.Start();
        }
    }
}
