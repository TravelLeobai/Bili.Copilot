// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型 - 直播.
/// 参考 BiliLite 的直播详情页，补齐直播弹幕、在线人数、关注与清晰度等能力.
/// </summary>
public sealed partial class PlayerViewModel
{
    private ClientWebSocket? _liveSocket;
    private CancellationTokenSource? _liveCancellationTokenSource;
    private int _liveHeartbeatSeconds = 30;

    /// <summary>
    /// 直播弹幕列表.
    /// </summary>
    public ObservableCollection<LiveDanmakuItem> LiveDanmakus { get; } = new();

    /// <summary>
    /// 当前是否在播放直播.
    /// </summary>
    [ObservableProperty]
    private bool _isLive;

    /// <summary>
    /// 直播标题.
    /// </summary>
    [ObservableProperty]
    private string _liveTitle = string.Empty;

    /// <summary>
    /// 直播 UP 主.
    /// </summary>
    [ObservableProperty]
    private UserProfile? _liveUser;

    /// <summary>
    /// 在线人气.
    /// </summary>
    [ObservableProperty]
    private string _liveOnlineText = "0";

    /// <summary>
    /// 直播弹幕输入内容.
    /// </summary>
    [ObservableProperty]
    private string _liveDanmakuInput = string.Empty;

    /// <summary>
    /// 是否已关注主播.
    /// </summary>
    [ObservableProperty]
    private bool _isLiveUserFollowed;

    /// <summary>
    /// 直播播放地址状态提示.
    /// </summary>
    [ObservableProperty]
    private string _liveStatusText = string.Empty;

    private string LiveRoomId => _snapshot?.Live?.Identifier.Id ?? string.Empty;

    private async Task ConnectLiveAsync(CancellationToken cancellationToken)
    {
        if (_snapshot?.Live is not null)
        {
            LiveTitle = _snapshot.Live.Identifier.Title;
            LiveUser = _snapshot.Live.User;
            IsLiveUserFollowed = _snapshot.Live.Relation is UserRelationStatus.Following or UserRelationStatus.Friends or UserRelationStatus.SpeciallyFollowed;
        }

        if (string.IsNullOrEmpty(LiveRoomId))
        {
            return;
        }

        _liveCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _liveCancellationTokenSource.Token;
        try
        {
            var service = this.Get<IPlayerService>();
            _liveSocket = await service.EnterLiveRoomAsync(LiveRoomId, token);
            LiveStatusText = "弹幕已连接";
            _ = HeartbeatLoopAsync(service, token);
            while (!token.IsCancellationRequested && _liveSocket is { State: WebSocketState.Open })
            {
                var messages = await service.GetLiveSocketMessagesAsync(_liveSocket, token);
                if (messages is null || messages.Count == 0)
                {
                    await Task.Delay(200, token);
                    continue;
                }

                foreach (var message in messages)
                {
                    HandleLiveMessage(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LiveStatusText = "弹幕连接失败";
            logger.LogWarning(ex, "直播弹幕连接失败.");
        }
    }

    private async Task HeartbeatLoopAsync(IPlayerService service, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _liveSocket is { State: WebSocketState.Open })
            {
                await Task.Delay(TimeSpan.FromSeconds(_liveHeartbeatSeconds), token);
                await service.SendLiveHeartBeatAsync(_liveSocket, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "直播心跳发送失败.");
        }
    }

    private void HandleLiveMessage(LiveMessage message)
    {
        switch (message.Type)
        {
            case LiveMessageType.Danmaku when message.Data is LiveDanmakuInformation danmaku:
                queue.TryEnqueue(() => AppendLiveDanmaku(danmaku));
                break;
            case LiveMessageType.Online when message.Data is not null:
                queue.TryEnqueue(() => LiveOnlineText = message.Data.ToString() ?? "0");
                break;
            case LiveMessageType.Welcome when message.Data is LiveDanmakuInformation welcome:
                queue.TryEnqueue(() => AppendLiveDanmaku(welcome));
                break;
            case LiveMessageType.Gift when message.Data is LiveDanmakuInformation gift:
                queue.TryEnqueue(() => AppendLiveDanmaku(gift));
                break;
            case LiveMessageType.SuperChat when message.Data is LiveDanmakuInformation superChat:
                queue.TryEnqueue(() => AppendLiveDanmaku(superChat));
                break;
            default:
                break;
        }
    }

    private void AppendLiveDanmaku(LiveDanmakuInformation information)
    {
        LiveDanmakus.Add(new LiveDanmakuItem(information.UserName, information.Text, information.UserLevel));
        while (LiveDanmakus.Count > 200)
        {
            LiveDanmakus.RemoveAt(0);
        }
    }

    [RelayCommand]
    private async Task SendLiveDanmakuAsync()
    {
        var text = LiveDanmakuInput?.Trim();
        if (string.IsNullOrEmpty(text) || _liveSocket is null || _liveSocket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            var service = this.Get<IPlayerService>();
            var payload = JsonSerializer.Serialize(new
            {
                color = 16777215,
                fontsize = 25,
                mode = 1,
                msg = text,
                rnd = DateTimeOffset.Now.ToUnixTimeSeconds(),
                roomid = Convert.ToInt64(LiveRoomId),
                biz = 0,
            });

            await service.SendLiveMessageAsync(_liveSocket, payload, 5, _liveCancellationTokenSource?.Token ?? default);
            LiveDanmakuInput = string.Empty;
        }
        catch (Exception ex)
        {
            LiveStatusText = "弹幕发送失败";
            logger.LogWarning(ex, "发送直播弹幕失败.");
        }
    }

    [RelayCommand]
    private async Task ToggleLiveFollowAsync()
    {
        if (LiveUser is null)
        {
            return;
        }

        try
        {
            var service = this.Get<IRelationshipService>();
            if (IsLiveUserFollowed)
            {
                await service.UnfollowUserAsync(LiveUser.Id, default);
            }
            else
            {
                await service.FollowUserAsync(LiveUser.Id, default);
            }

            IsLiveUserFollowed = !IsLiveUserFollowed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "切换直播关注状态失败.");
        }
    }

    private void StopLive()
    {
        try
        {
            _liveCancellationTokenSource?.Cancel();
            _liveSocket?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "关闭直播连接时出错.");
        }
        finally
        {
            _liveCancellationTokenSource?.Dispose();
            _liveSocket = null;
            _liveCancellationTokenSource = null;
            LiveDanmakus.Clear();
        }
    }
}

/// <summary>
/// 直播弹幕条目.
/// </summary>
public sealed class LiveDanmakuItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveDanmakuItem"/> class.
    /// </summary>
    public LiveDanmakuItem(string userName, string text, string level)
    {
        UserName = userName;
        Text = text;
        Level = level;
    }

    /// <summary>
    /// 用户名.
    /// </summary>
    public string UserName { get; }

    /// <summary>
    /// 弹幕内容.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// 用户等级.
    /// </summary>
    public string Level { get; }
}
