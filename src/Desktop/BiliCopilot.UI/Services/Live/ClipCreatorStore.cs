// Copyright (c) Bili Copilot. All rights reserved.

using System.Text.Json;
using Windows.Storage;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 切片 UP 主的选择存成一个 JSON 文件（放在 LocalState 里）。
/// 之前用 SettingsToolkit 写 LocalSettings，写完应用就会启动即崩，
/// 而且 uninstall 都救不回来 —— 换成普通文件，坏了大不了删掉重来.
/// </summary>
public sealed class ClipCreatorStore
{
    private const string FileName = "live-clip-creators.json";

    /// <summary>
    /// 读取保存的切片 UP 主.
    /// </summary>
    /// <returns>id 列表.</returns>
    public static HashSet<string> Load()
    {
        try
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
            if (!File.Exists(path))
            {
                return [];
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<ClipCreatorFile>(json);
            return data?.Ids is null ? [] : [.. data.Ids];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 保存切片 UP 主（id + 名字）.
    /// </summary>
    /// <param name="ids">id 集合.</param>
    /// <param name="names">id → 名字.</param>
    public static void Save(HashSet<string> ids, Dictionary<string, string> names)
    {
        try
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
            var payload = new ClipCreatorFile
            {
                Ids = [.. ids],
                Names = names,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload));
        }
        catch
        {
            // 存不下来就算了，不影响当前会话.
        }
    }

    private sealed class ClipCreatorFile
    {
        public List<string> Ids { get; set; } = [];

        public Dictionary<string, string> Names { get; set; } = [];
    }
}
