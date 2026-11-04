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
}
