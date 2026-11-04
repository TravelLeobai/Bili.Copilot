// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 把 CDN 优选的执行状态以弹幕形式显示在播放界面.
/// </summary>
public sealed partial class PlayerViewModel
{
    private bool _isCdnStatusHooked;

    private void HookCdnStatus()
    {
        if (_isCdnStatusHooked)
        {
            return;
        }

        _isCdnStatusHooked = true;
        CdnStatusBus.StatusReported += OnCdnStatusReported;
    }

    private void OnCdnStatusReported(object? sender, string message)
    {
        if (IsLive || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // 走弹幕通道，播放界面上直接滚一条出来（弹幕控件自己会回 UI 线程处理）.
        Danmaku.AddDanmaku($"【CDN】{message}");
    }
}
