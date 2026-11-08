// Copyright (c) Bili Copilot. All rights reserved.

using Richasy.BiliKernel.Bili.Authorization;
using Richasy.BiliKernel.Bili.User;
using System.Text;
using System.Text.Json;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 关注 UP 主的开播状态查询。
/// BiliKernel 没有"关注直播列表"接口，所以自己拼：拉关注分组里的用户，再批量问开播状态。
/// </summary>
public sealed class LiveFollowService
{
    private const string LiveStatusApi = "https://api.live.bilibili.com/room/v1/Room/get_status_info_by_uids";

    // 一次最多查多少个关注用户.
    private const int MaxWatchedUsers = 200;

    private static readonly string[] UserAgent =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
    ];

    /// <summary>
    /// 拉取关注列表里正在直播的房间。
    /// </summary>
    /// <param name="cookiesResolver">Cookie 解析器.</param>
    /// <param name="relationshipService">关注关系服务.</param>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>在播房间；拿不到返回空列表.</returns>
    public static async Task<List<FollowedLiveRoom>> GetLiveRoomsAsync(
        IBiliCookiesResolver cookiesResolver,
        IRelationshipService relationshipService,
        CancellationToken cancellationToken = default)
    {
        var rooms = new List<FollowedLiveRoom>();
        var userIds = await GetFollowedUserIdsAsync(relationshipService, cancellationToken).ConfigureAwait(false);
        if (userIds.Count == 0)
        {
            return rooms;
        }

        var cookie = cookiesResolver?.GetCookieString() ?? string.Empty;
        if (string.IsNullOrEmpty(cookie))
        {
            return rooms;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var seen = new HashSet<long>();
        foreach (var chunk in userIds.Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uids = chunk.Where(p => long.TryParse(p, out _)).Select(long.Parse).Where(p => p > 0).ToList();
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
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent[0]);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ParseLiveStatus(json, rooms, seen);
        }

        return rooms;
    }

    /// <summary>
    /// 关注用户 id 列表（分组拉取，总量封顶）.
    /// </summary>
    /// <param name="relationshipService">关注关系服务.</param>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>用户 id 列表.</returns>
    public static async Task<List<string>> GetFollowedUserIdsAsync(
        IRelationshipService relationshipService,
        CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        if (relationshipService is null)
        {
            return ids;
        }

        var groups = await relationshipService.GetMyFollowUserGroupsAsync().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
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
            for (var page = 0; page < 3 && ids.Count < MaxWatchedUsers; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (users, nextOffset) = await relationshipService
                    .GetMyFollowUserGroupDetailAsync(group.Id, pn)
                    .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                    .ConfigureAwait(false);

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

        return ids;
    }

    private static void ParseLiveStatus(string json, List<FollowedLiveRoom> rooms, HashSet<long> seen)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("code", out var codeElement) || codeElement.GetInt32() != 0)
            {
                return;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var entry in data.EnumerateObject())
            {
                var item = entry.Value;
                if (item.ValueKind != JsonValueKind.Object || ReadLong(item, "live_status") != 1)
                {
                    continue;
                }

                var roomId = ReadLong(item, "room_id", "roomid");
                if (roomId <= 0 || !seen.Add(roomId))
                {
                    continue;
                }

                rooms.Add(new FollowedLiveRoom(
                    roomId,
                    ReadLong(item, "uid"),
                    ReadString(item, "uname") ?? "关注的主播",
                    ReadString(item, "title") ?? string.Empty,
                    ReadString(item, "face", "keyframe", "cover") ?? string.Empty,
                    ReadLong(item, "online")));
            }
        }
        catch
        {
            // 解析失败就当这批没有在播的.
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
}

/// <summary>
/// 一个正在直播的关注房间.
/// </summary>
/// <param name="RoomId">房间号.</param>
/// <param name="UserId">主播 uid.</param>
/// <param name="UserName">主播名.</param>
/// <param name="Title">直播间标题.</param>
/// <param name="Face">主播头像.</param>
/// <param name="Online">人气.</param>
public sealed record FollowedLiveRoom(long RoomId, long UserId, string UserName, string Title, string Face, long Online);
