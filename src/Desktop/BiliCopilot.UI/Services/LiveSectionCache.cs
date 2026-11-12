// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 动态-直播分区的内容缓存：落一份 JSON 到 LocalState，
/// 切走再切回来、甚至重开应用都能先把上次的卡片铺出来，不用白等一轮网络.
/// 卡片本身是纯数据（标题/封面/作者/badge/房间号/BV 号），跨 ViewModel 复用是安全的.
/// </summary>
internal static class LiveSectionCache
{
    private const string FileName = "live-section-cache.json";

    private static readonly List<LiveSectionCardViewModel> CachedCards = [];
    private static bool _loaded;

    /// <summary>
    /// 上次成功加载的时间（本地时间）.
    /// </summary>
    public static DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// 缓存里有没有东西.
    /// </summary>
    public static bool HasContent => CachedCards.Count > 0;

    /// <summary>
    /// 缓存超过这个时长就该在后台刷一次.
    /// </summary>
    public static TimeSpan StaleAfter { get; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 是否已经过期.
    /// </summary>
    public static bool IsStale => DateTimeOffset.Now - UpdatedAt > StaleAfter;

    /// <summary>
    /// 取缓存快照（返回的是同一批卡片对象，调用方不要再往里加）.
    /// </summary>
    /// <returns>卡片列表.</returns>
    public static IReadOnlyList<LiveSectionCardViewModel> GetCards() => CachedCards;

    /// <summary>
    /// 第一次用到时从磁盘读一次缓存.
    /// </summary>
    /// <returns>等待任务.</returns>
    public static async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
            {
                return;
            }

            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            CachedCards.Clear();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var dto = FromLine(line);
                if (dto is not null)
                {
                    CachedCards.Add(ToViewModel(dto));
                }
            }

            if (CachedCards.Count == 0)
            {
                return;
            }

            UpdatedAt = new DateTimeOffset(File.GetLastWriteTime(path));
            Serilog.Log.Information("直播分区缓存：从磁盘读到 {Count} 张", CachedCards.Count);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "直播分区缓存读取失败");
        }
    }

    /// <summary>
    /// 用当前内容覆盖缓存，并写回磁盘.
    /// </summary>
    /// <param name="cards">当前全部卡片.</param>
    public static void Save(IEnumerable<LiveSectionCardViewModel> cards)
    {
        CachedCards.Clear();
        CachedCards.AddRange(cards);
        UpdatedAt = DateTimeOffset.Now;
        Serilog.Log.Information("直播分区缓存：存下 {Count} 张", CachedCards.Count);

        try
        {
            var lines = CachedCards.Select(c => ToLine(ToDto(c)));
            File.WriteAllLines(GetFilePath(), lines);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "直播分区缓存写入失败");
        }
    }

    /// <summary>
    /// 清空缓存（登出时用）.
    /// </summary>
    public static void Clear()
    {
        CachedCards.Clear();
        UpdatedAt = DateTimeOffset.MinValue;
        try
        {
            var path = GetFilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "直播分区缓存清理失败");
        }
    }

    private static string GetFilePath()
        => Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, FileName);

    private static LiveSectionCardViewModel ToViewModel(CachedCard dto)
        => new(
            dto.Title,
            dto.Cover,
            dto.AuthorName,
            dto.AuthorId,
            dto.Badge,
            dto.IsLive,
            dto.Detail,
            string.IsNullOrEmpty(dto.Avatar) ? null : new Uri(dto.Avatar))
        {
            RoomId = dto.RoomId,
            Bvid = dto.Bvid ?? string.Empty,
            IsClip = dto.IsClip,
        };

    private static CachedCard ToDto(LiveSectionCardViewModel card)
        => new(
            card.Title,
            card.Cover,
            card.AuthorName,
            card.AuthorId,
            card.Badge,
            card.IsLive,
            card.Detail,
            card.Avatar?.ToString(),
            card.RoomId,
            card.Bvid,
            card.IsClip);

    /// <summary>
    /// 一行一条：字段用 \t 分隔，字段内的 \t / \n / \r 先转义.
    /// （本应用裁掉了反射式 JSON 序列化，JsonSerializer 在这里会直接抛异常，所以手写格式.）
    /// </summary>
    private static string ToLine(CachedCard card)
    {
        string[] fields =
        [
            card.IsLive ? "1" : "0",
            card.RoomId.ToString(),
            card.Bvid ?? string.Empty,
            card.IsClip ? "1" : "0",
            card.Title,
            card.Cover,
            card.AuthorName,
            card.AuthorId,
            card.Badge,
            card.Detail,
            card.Avatar ?? string.Empty,
        ];
        return string.Join('\t', fields.Select(Escape));
    }

    private static CachedCard? FromLine(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 11)
        {
            return null;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Unescape(parts[i]);
        }

        return new CachedCard(
            parts[4],
            parts[5],
            parts[6],
            parts[7],
            parts[8],
            parts[0] == "1",
            parts[9],
            string.IsNullOrEmpty(parts[10]) ? null : parts[10],
            long.TryParse(parts[1], out var roomId) ? roomId : 0,
            parts[2],
            parts[3] == "1");
    }

    private static string Escape(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\t", "\\t")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");

    private static string Unescape(string value)
        => value
            .Replace("\\t", "\t")
            .Replace("\\r", "\r")
            .Replace("\\n", "\n")
            .Replace("\\\\", "\\");

    /// <summary>
    /// 落盘用的扁平板卡数据.
    /// </summary>
    private sealed record CachedCard(
        string Title,
        string Cover,
        string AuthorName,
        string AuthorId,
        string Badge,
        bool IsLive,
        string Detail,
        string? Avatar,
        long RoomId,
        string Bvid,
        bool IsClip);
}
