// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.Media;
using System.Text.Json;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 订阅合集更新提醒.
/// 订阅合集接口不返回「最近更新」，所以拿每个合集第一页里发布时间最新的那条当基准，
/// 和上一次的快照比对：出现了新的 bvid 就弹通知.
/// </summary>
public sealed partial class NotificationViewModel
{
    // 一次轮询最多看多少个合集，避免订阅几百个时把接口打爆.
    private const int MaxWatchedSeasons = 30;

    [RelayCommand]
    private async Task TrySendSeasonUpdateNotificationsAsync()
    {
        if (!SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsSeasonUpdateNotificationEnabled, true))
        {
            return;
        }

        try
        {
            var service = this.Get<IFavoriteService>();
            var (groups, _) = await service.GetVideoFavoriteGroupsAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (groups is null)
            {
                return;
            }

            var seasons = groups
                .SelectMany(p => p.Folders)
                .Where(p => p.IsUgcSeason == true)
                .Take(MaxWatchedSeasons)
                .ToList();
            if (seasons.Count == 0)
            {
                return;
            }

            var previous = LoadSeasonBaseline();
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var updated = new List<(VideoFavoriteFolder Folder, VideoInformation Video)>();

            foreach (var season in seasons)
            {
                try
                {
                    var (videos, _, _) = await service
                        .GetUgcSeasonVideosAsync(season, 0)
                        .WaitAsync(TimeSpan.FromSeconds(20));
                    var latest = videos?
                        .Where(p => !string.IsNullOrEmpty(p.Identifier.Id))
                        .OrderByDescending(p => p.PublishTime ?? DateTimeOffset.MinValue)
                        .FirstOrDefault();
                    if (latest is null)
                    {
                        continue;
                    }

                    current[season.Id] = latest.Identifier.Id;
                    if (previous.Count > 0
                        && previous.TryGetValue(season.Id, out var lastId)
                        && !string.Equals(lastId, latest.Identifier.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        updated.Add((season, latest));
                    }
                }
                catch
                {
                    // 单个合集失败不影响其它合集的比对.
                }

                await Task.Delay(150).ConfigureAwait(false);
            }

            if (previous.Count == 0)
            {
                SaveSeasonBaseline(current);
                _logger.LogInformation("合集更新提醒：首次建立基线，共 {Count} 个合集", current.Count);
                return;
            }

            foreach (var (folder, video) in updated.Take(3))
            {
                ShowSeasonUpdateToast(folder, video);
                await Task.Delay(800).ConfigureAwait(false);
            }

            if (updated.Count > 0)
            {
                _logger.LogInformation("合集更新提醒：本轮发现 {Count} 个合集有新视频", updated.Count);
            }

            SaveSeasonBaseline(current);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查订阅合集更新失败");
        }
    }

    /// <summary>
    /// 基线用「合集id=最新视频id;…」这种平铺字符串存。
    /// 走 JsonSerializer 会撞上「反射序列化已禁用」（应用是 trimming/AOT 配置），格式简单就用简单格式.
    /// </summary>
    private static Dictionary<string, string> LoadSeasonBaseline()
    {
        var raw = Services.AppStateStore.GetString(Models.Constants.SettingNames.LastSeasonVideoIds.ToString());
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw))
        {
            return result;
        }

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = entry.IndexOf('=');
            if (index <= 0 || index >= entry.Length - 1)
            {
                continue;
            }

            result[entry[..index]] = entry[(index + 1)..];
        }

        return result;
    }

    private static void SaveSeasonBaseline(Dictionary<string, string> baseline)
        => Services.AppStateStore.Set(
            Models.Constants.SettingNames.LastSeasonVideoIds.ToString(),
            string.Join(';', baseline.Select(p => $"{p.Key}={p.Value}")));

    private static void ShowSeasonUpdateToast(VideoFavoriteFolder folder, VideoInformation video)
    {
        try
        {
            var title = string.IsNullOrEmpty(folder.Title) ? "订阅的合集" : folder.Title;
            var builder = new AppNotificationBuilder()
                .AddArgument("page", "season")
                .AddArgument("args", string.Join('|', folder.Id, video.Identifier.Id))
                .AddText($"合集「{title}」更新了")
                .AddText(string.IsNullOrEmpty(video.Identifier.Title) ? "有新视频" : video.Identifier.Title);

            var cover = folder.Cover?.Uri?.ToString();
            if (!string.IsNullOrEmpty(cover) && Uri.TryCreate(cover, UriKind.Absolute, out var coverUri))
            {
                builder = builder.SetAppLogoOverride(coverUri, AppNotificationImageCrop.Circle);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch
        {
            // 通知发不出去不影响主流程（用户可能关了系统通知）.
        }
    }
}


