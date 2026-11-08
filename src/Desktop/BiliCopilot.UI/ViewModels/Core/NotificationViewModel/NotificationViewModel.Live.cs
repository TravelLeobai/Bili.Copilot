// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.User;
using Richasy.BiliKernel.Models.User;
using System.Text;
using System.Text.Json;

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 关注的主播开播提醒。
/// BiliKernel 没有"关注直播列表"接口，所以这里自己拼：分组拉关注用户 → 批量查开播状态。
/// 每次轮询只和上一次的快照比对，新出现的房间才提醒，避免重复弹通知。
/// </summary>
public sealed partial class NotificationViewModel
{
    private const string LiveStatusApi = "https://api.live.bilibili.com/room/v1/Room/get_status_info_by_uids";

    // 一次轮询最多看多少个关注用户，防止关注几百人时把接口打爆.
    private const int MaxWatchedUsers = 200;

    [RelayCommand]
    private async Task TrySendLiveStartNotificationsAsync()
    {
        if (!SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsLiveStartNotificationEnabled, true))
        {
            return;
        }

        try
        {
            var userIds = await CollectFollowedUserIdsAsync();
            if (userIds.Count == 0)
            {
                return;
            }

            var rooms = await QueryLiveRoomsAsync(userIds);
            if (rooms is null)
            {
                // 拉取失败就别动快照，否则下次会把一堆"新开播"误报出来.
                return;
            }

            var currentIds = rooms.Select(p => p.RoomId.ToString()).ToList();
            var previousRaw = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.LastLiveRoomIds, string.Empty);
            var previousIds = string.IsNullOrEmpty(previousRaw)
                ? []
                : previousRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

            if (previousIds.Count == 0)
            {
                SaveLiveRoomIds(currentIds);
                _logger.LogInformation("开播提醒：首次建立基线，当前在播 {Count} 个", currentIds.Count);
                return;
            }

            var newRooms = rooms
                .Where(p => !previousIds.Contains(p.RoomId.ToString()))
                .Take(3)
                .ToList();

            foreach (var room in newRooms)
            {
                ShowLiveStartToast(room);
                await Task.Delay(800).ConfigureAwait(false);
            }

            if (newRooms.Count > 0)
            {
                _logger.LogInformation("开播提醒：本次新开播 {Count} 个", newRooms.Count);
            }

            SaveLiveRoomIds(currentIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查关注主播开播状态失败");
        }
    }

    private void SaveLiveRoomIds(List<string> ids)
        => SettingsToolkit.WriteLocalSetting(Models.Constants.SettingNames.LastLiveRoomIds, string.Join(',', ids));

    private static void ShowLiveStartToast(LiveRoomBrief room)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("page", "live")
                .AddArgument("args", string.Join('|', room.RoomId, room.UserId, room.UserName, room.Face, room.Title))
                .AddText($"{room.UserName} 开播了")
                .AddText(string.IsNullOrEmpty(room.Title) ? "正在直播" : room.Title);

            if (!string.IsNullOrEmpty(room.Face) && Uri.TryCreate(room.Face, UriKind.Absolute, out var faceUri))
            {
                builder = builder.SetAppLogoOverride(faceUri, AppNotificationImageCrop.Circle);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch
        {
            // 通知发不出去不影响主流程（用户可能关了系统通知）.
        }
    }

    /// <summary>
    /// 把关注分组里的用户 id 收集起来（每个分组最多翻几页，总量封顶）.
    /// </summary>
    private async Task<List<string>> CollectFollowedUserIdsAsync()
    {
        var ids = new List<string>();
        var service = this.Get<IRelationshipService>();
        var groups = await service.GetMyFollowUserGroupsAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (groups is null)
        {
            return ids;
        }

        foreach (var group in groups)
        {
            if (ids.Count >= MaxWatchedUsers)
            {
                break;
            }

            var pn = 0;
            // 每组最多翻 3 页，够覆盖大部分人了，也不用把所有人拉一遍.
            for (var page = 0; page < 3 && ids.Count < MaxWatchedUsers; page++)
            {
                var (users, nextOffset) = await service
                    .GetMyFollowUserGroupDetailAsync(group.Id, pn)
                    .WaitAsync(TimeSpan.FromSeconds(15));

                if (users is null || users.Count == 0)
                {
                    break;
                }

                foreach (var user in users)
                {
                    var id = user.Profile.User.Id;
                    if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }

                pn = nextOffset;
            }
        }

        _logger.LogInformation("开播提醒：本轮检查 {Count} 个关注用户", ids.Count);
        return ids;
    }

    /// <summary>
    /// 批量查询这些用户里谁在直播.
    /// </summary>
    private async Task<List<LiveRoomBrief>?> QueryLiveRoomsAsync(List<string> userIds)
    {
        var cookie = this.Get<IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;
        if (string.IsNullOrEmpty(cookie))
        {
            _logger.LogDebug("开播提醒：没有登录 Cookie，跳过");
            return null;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var rooms = new List<LiveRoomBrief>();
        var seen = new HashSet<long>();

        foreach (var chunk in userIds.Chunk(50))
        {
            var uids = chunk.Select(p => long.TryParse(p, out var value) ? value : 0).Where(p => p > 0).ToList();
            if (uids.Count == 0)
            {
                continue;
            }

            var body = new StringBuilder("{\"uids\":[");
            body.Append(string.Join(',', uids));
            body.Append("]}");

            using var request = new HttpRequestMessage(HttpMethod.Post, LiveStatusApi)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("开播提醒：状态接口返回 {Code}", (int)response.StatusCode);
                continue;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            ParseLiveStatus(json, rooms, seen);
        }

        return rooms;
    }

    private void ParseLiveStatus(string json, List<LiveRoomBrief> rooms, HashSet<long> seen)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("code", out var codeElement) || codeElement.GetInt32() != 0)
            {
                _logger.LogWarning("开播提醒：状态接口返回异常 {Json}", Truncate(json));
                return;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("开播提醒：状态接口没有 data {Json}", Truncate(json));
                return;
            }

            foreach (var entry in data.EnumerateObject())
            {
                var item = entry.Value;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var liveStatus = ReadLong(item, "live_status");
                if (liveStatus != 1)
                {
                    continue;
                }

                var roomId = ReadLong(item, "room_id", "roomid");
                if (roomId <= 0 || !seen.Add(roomId))
                {
                    continue;
                }

                rooms.Add(new LiveRoomBrief(
                    roomId,
                    ReadLong(item, "uid"),
                    ReadString(item, "uname") ?? "关注的主播",
                    ReadString(item, "title") ?? string.Empty,
                    ReadString(item, "face", "keyframe", "cover") ?? string.Empty));
            }

            _logger.LogInformation("开播提醒：本批在播 {Live} 个", rooms.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "开播提醒：解析状态失败 {Json}", Truncate(json));
        }
    }

    private static long ReadLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string Truncate(string text)
        => text.Length <= 300 ? text : text[..300] + "…";

    private sealed record LiveRoomBrief(long RoomId, long UserId, string UserName, string Title, string Face);
}


