// Copyright (c) Bili Copilot. All rights reserved.

using System.Text.Json;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 直播间表情的两份用户数据：「我的表情」（手动收藏）和「最近使用」.
/// 存在 LocalState 下的一个 json 里，和设置项分开，避免污染 SettingsToolkit.
/// </summary>
internal static class LiveEmoteStore
{
    private const string FileName = "live-emotes.json";

    /// <summary>
    /// 最近使用最多保留的数量.
    /// </summary>
    private const int RecentLimit = 24;

    private static readonly object SyncRoot = new();

    private static EmoteState? _state;

    /// <summary>
    /// 收藏的表情发生变化（新增/移除/记一次使用）.
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// 我的表情.
    /// </summary>
    public static IReadOnlyList<StoredEmote> Mine
    {
        get
        {
            EnsureLoaded();
            return _state!.Mine;
        }
    }

    /// <summary>
    /// 最近使用.
    /// </summary>
    public static IReadOnlyList<StoredEmote> Recent
    {
        get
        {
            EnsureLoaded();
            return _state!.Recent;
        }
    }

    /// <summary>
    /// 判断某个表情是否已收藏.
    /// </summary>
    /// <param name="key">表情唯一键.</param>
    /// <returns>是否已收藏.</returns>
    public static bool IsMine(string key)
    {
        EnsureLoaded();
        return _state!.Mine.Any(p => string.Equals(p.Key, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// 加入我的表情.
    /// </summary>
    /// <param name="emote">表情.</param>
    public static void AddMine(StoredEmote emote)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(emote.Key) || IsMine(emote.Key))
        {
            return;
        }

        _state!.Mine.Insert(0, emote);
        SaveAndNotify();
    }

    /// <summary>
    /// 从我的表情里移除.
    /// </summary>
    /// <param name="key">表情唯一键.</param>
    public static void RemoveMine(string key)
    {
        EnsureLoaded();
        var removed = _state!.Mine.RemoveAll(p => string.Equals(p.Key, key, StringComparison.Ordinal));
        if (removed > 0)
        {
            SaveAndNotify();
        }
    }

    /// <summary>
    /// 记一次使用.
    /// </summary>
    /// <param name="emote">表情.</param>
    public static void AddRecent(StoredEmote emote)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(emote.Key))
        {
            return;
        }

        _state!.Recent.RemoveAll(p => string.Equals(p.Key, emote.Key, StringComparison.Ordinal));
        _state.Recent.Insert(0, emote);
        while (_state.Recent.Count > RecentLimit)
        {
            _state.Recent.RemoveAt(_state.Recent.Count - 1);
        }

        SaveAndNotify();
    }

    private static void SaveAndNotify()
    {
        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void EnsureLoaded()
    {
        if (_state is not null)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_state is not null)
            {
                return;
            }

            var path = GetFilePath();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _state = JsonSerializer.Deserialize<EmoteState>(json) ?? new EmoteState();
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "读取直播表情收藏失败");
            }

            _state ??= new EmoteState();
        }
    }

    private static void Save()
    {
        try
        {
            var path = GetFilePath();
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                _ = Directory.CreateDirectory(folder);
            }

            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存直播表情收藏失败");
        }
    }

    private static string GetFilePath()
        => Path.Combine(
            Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path,
            FileName);

    private sealed class EmoteState
    {
        public List<StoredEmote> Mine { get; set; } = [];

        public List<StoredEmote> Recent { get; set; } = [];
    }
}

/// <summary>
/// 存下来的一个表情.
/// </summary>
internal sealed class StoredEmote
{
    /// <summary>
    /// 唯一键：直播间表情用 emoticon_unique，普通表情用 token.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 显示文本.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 图片地址.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// 直播间表情的 emoticon_unique，空表示普通表情.
    /// </summary>
    public string Unique { get; set; } = string.Empty;

    /// <summary>
    /// 是否未解锁.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// 从直播间表情转换.
    /// </summary>
    /// <param name="item">直播间表情.</param>
    /// <returns>存下来的表情.</returns>
    public static StoredEmote FromLive(Live.LiveEmoteItem item) => new()
    {
        Key = string.IsNullOrEmpty(item.Unique) ? item.Text : item.Unique,
        Text = item.Text,
        Url = item.Url,
        Unique = item.Unique,
        IsLocked = item.IsLocked,
    };

    /// <summary>
    /// 从服务端表情包转换.
    /// </summary>
    /// <param name="token">token.</param>
    /// <param name="url">图片地址.</param>
    /// <param name="text">显示文本.</param>
    /// <returns>存下来的表情.</returns>
    public static StoredEmote FromToken(string token, string url, string? text = null) => new()
    {
        Key = token,
        Text = string.IsNullOrEmpty(text) ? token : text,
        Url = url,
    };
}
