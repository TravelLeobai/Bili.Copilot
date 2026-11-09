// Copyright (c) Bili Copilot. All rights reserved.

using System.Text.Json;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 应用自己的界面状态（上次分区、通知基线等）存成一个普通 JSON 文件。
/// 之前这些都写在 LocalSettings 里，切分区、通知轮询都会写一次；
/// 包设置状态容器一旦出问题（事件日志里的 SettingsInitialize / 80073DB8），
/// 登录 Token 也跟着读不出来，下次启动就退到登录窗口 —— 表现就是"打不开"。
/// 换成本地文件，写坏了大不了删掉，不会连累设置容器.
/// </summary>
public static class AppStateStore
{
    private static readonly object SyncRoot = new();
    private static Dictionary<string, string>? _cache;

    private static string FilePath
    {
        get
        {
            try
            {
                return Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "app-state.json");
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "app-state.json");
            }
        }
    }

    /// <summary>
    /// 读一个字符串状态.
    /// </summary>
    /// <param name="key">键.</param>
    /// <param name="defaultValue">默认值.</param>
    /// <returns>值.</returns>
    public static string GetString(string key, string defaultValue = "")
        => Load().TryGetValue(key, out var value) ? value : defaultValue;

    /// <summary>
    /// 读一个整数状态.
    /// </summary>
    /// <param name="key">键.</param>
    /// <param name="defaultValue">默认值.</param>
    /// <returns>值.</returns>
    public static int GetInt(string key, int defaultValue = 0)
        => Load().TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;

    /// <summary>
    /// 读一个布尔状态.
    /// </summary>
    /// <param name="key">键.</param>
    /// <param name="defaultValue">默认值.</param>
    /// <returns>值.</returns>
    public static bool GetBool(string key, bool defaultValue = false)
        => Load().TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    /// <summary>
    /// 写一个状态（立即落盘；量很小）.
    /// </summary>
    /// <param name="key">键.</param>
    /// <param name="value">值.</param>
    public static void Set(string key, string value)
    {
        var data = Load();
        if (data.TryGetValue(key, out var existing) && existing == value)
        {
            return;
        }

        data[key] = value;
        Save(data);
    }

    /// <summary>
    /// 写一个整数状态.
    /// </summary>
    /// <param name="key">键.</param>
    /// <param name="value">值.</param>
    public static void Set(string key, int value) => Set(key, value.ToString());

    /// <summary>
    /// 写一个布尔状态.
    /// </summary>
    /// <param name="key">键.</param>
    /// <param name="value">值.</param>
    public static void Set(string key, bool value) => Set(key, value ? "true" : "false");

    private static Dictionary<string, string> Load()
    {
        lock (SyncRoot)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
                }
                else
                {
                    _cache = [];
                }
            }
            catch
            {
                _cache = [];
            }

            return _cache;
        }
    }

    private static void Save(Dictionary<string, string> data)
    {
        lock (SyncRoot)
        {
            try
            {
                var path = FilePath;
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllText(path, JsonSerializer.Serialize(data));
            }
            catch
            {
                // 写不下来就算了，不影响当前会话.
            }
        }
    }
}
