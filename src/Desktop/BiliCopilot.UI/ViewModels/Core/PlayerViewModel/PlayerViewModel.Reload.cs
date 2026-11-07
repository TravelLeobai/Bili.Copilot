// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器重载.
/// </summary>
public sealed partial class PlayerViewModel
{
    /// <summary>
    /// 用同一份快照重走一遍播放初始化（等于把当前视频重新加载）.
    /// </summary>
    /// <returns>任务.</returns>
    public async Task ReloadAsync()
    {
        if (_snapshot is null)
        {
            logger.LogWarning("重载播放器时没有可用的媒体快照，忽略");
            return;
        }

        logger.LogInformation("重载播放器：{Type}", _snapshot.Type);
        await InitializeAsync(_snapshot);
    }

    /// <summary>
    /// 是否已经播放到片尾（留 0.5 秒容差）.
    /// </summary>
    /// <returns>是否到片尾.</returns>
    internal bool IsPlayerAtEnd()
    {
        if (Player is null)
        {
            return false;
        }

        var duration = Player.Duration;
        return duration > 1 && Player.Position >= duration - 0.5;
    }

    /// <summary>
    /// 重新加载媒体并跳回指定位置（切清晰度、播完后重播都用它）.
    /// </summary>
    /// <param name="position">要跳回的位置，秒.</param>
    /// <returns>任务.</returns>
    internal async Task ReloadAtAsync(double position)
    {
        var target = Math.Max(0, position);
        logger.LogInformation("重新加载媒体，回到 {Position:F1}s", target);
        await ReloadAsync();

        if (Player is null || Player.IsWebEngine || target <= 0.5)
        {
            return;
        }

        // 新播放器刚初始化完，时长可能还没读到；夹一下避免跳到片尾之外.
        var duration = Player.Duration;
        if (duration > 1)
        {
            target = Math.Min(target, duration - 1);
        }

        try
        {
            await ApplySeekAsync(target);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "重新加载后跳转失败，从当前位置继续");
        }
    }
}
