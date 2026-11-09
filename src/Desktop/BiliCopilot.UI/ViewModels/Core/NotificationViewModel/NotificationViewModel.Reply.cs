// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using System.Security.Cryptography;
using System.Text;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 收到网友回复时的系统通知.
/// 每轮只拉第一页回复消息，用"时间+作者+内容"的摘要当作去重键，
/// 和历史快照比对，新出现的才弹通知.
/// </summary>
public sealed partial class NotificationViewModel
{
    private const int MaxReplyNotificationsPerRound = 3;

    [RelayCommand]
    private async Task TrySendReplyNotificationsAsync()
    {
        if (!SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsReplyNotificationEnabled, true))
        {
            return;
        }

        try
        {
            var service = this.Get<IMessageService>();
            var (messages, _, _, _) = await service
                .GetNotifyMessagesAsync(NotifyMessageType.Reply, 0, 0)
                .WaitAsync(TimeSpan.FromSeconds(15));

            if (messages is null || messages.Count == 0)
            {
                return;
            }

            var current = messages.Take(10).ToList();
            var currentKeys = current.Select(BuildReplyKey).ToList();
            var previousRaw = Services.AppStateStore.GetString(Models.Constants.SettingNames.LastReplyMessageKeys.ToString());
            var previousKeys = string.IsNullOrEmpty(previousRaw)
                ? []
                : previousRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

            if (previousKeys.Count == 0)
            {
                SaveReplyKeys(currentKeys);
                _logger.LogInformation("回复通知：首次建立基线，最近 {Count} 条", currentKeys.Count);
                return;
            }

            var newReplies = new List<(NotifyMessage Message, string Key)>();
            for (var i = 0; i < current.Count && newReplies.Count < MaxReplyNotificationsPerRound; i++)
            {
                if (!previousKeys.Contains(currentKeys[i]))
                {
                    newReplies.Add((current[i], currentKeys[i]));
                }
            }

            foreach (var (message, _) in newReplies)
            {
                ShowReplyToast(message);
                await Task.Delay(800).ConfigureAwait(false);
            }

            if (newReplies.Count > 0)
            {
                _logger.LogInformation("回复通知：本次新回复 {Count} 条", newReplies.Count);
            }

            SaveReplyKeys(currentKeys);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查回复通知失败");
        }
    }

    private void SaveReplyKeys(List<string> keys)
        => Services.AppStateStore.Set(Models.Constants.SettingNames.LastReplyMessageKeys.ToString(), string.Join(',', keys));

    /// <summary>
    /// 回复消息未必带稳定 id，用"发布时间 + 首个回复者 + 内容"算个短摘要当去重键.
    /// </summary>
    private static string BuildReplyKey(NotifyMessage message)
    {
        var author = message.Users?.FirstOrDefault()?.Name ?? string.Empty;
        var raw = $"{message.PublishTime?.ToUnixTimeSeconds() ?? 0}|{author}|{message.Message}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    private static void ShowReplyToast(NotifyMessage message)
    {
        try
        {
            var author = message.Users?.FirstOrDefault()?.Name ?? "有网友";
            var text = string.IsNullOrWhiteSpace(message.Message) ? "回复了你" : message.Message.Trim();
            if (text.Length > 80)
            {
                text = text[..80] + "…";
            }

            var builder = new AppNotificationBuilder()
                .AddText($"{author} 回复了你")
                .AddText(text);

            var face = message.Users?.FirstOrDefault()?.Avatar?.Uri;
            if (face is not null && Uri.TryCreate(face.ToString(), UriKind.Absolute, out var faceUri))
            {
                builder = builder.SetAppLogoOverride(faceUri, AppNotificationImageCrop.Circle);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch
        {
            // 通知失败不影响主流程.
        }
    }
}



