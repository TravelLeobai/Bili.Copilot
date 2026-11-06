// Copyright (c) Bili Copilot. All rights reserved.

using System.IO.Compression;
using Windows.Storage;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 一条背景记录：可以只有图片、只有视频，或两者都有.
/// </summary>
public sealed class BackgroundEntry
{
    /// <summary>
    /// 唯一 id，同时是存放资源的子目录名.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 显示名.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 动态背景（mp4）的本地完整路径.
    /// </summary>
    public string? VideoPath { get; set; }

    /// <summary>
    /// 静态背景（图片）的本地完整路径.
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    /// 列表里用的静态预览图（主题包的 image_cover 或用户传的图本身）.
    /// </summary>
    public string? PreviewPath { get; set; }

    /// <summary>
    /// 是否是「透明」这一项，需要它才能让用户主动选回无背景.
    /// </summary>
    public bool IsTransparent { get; set; }
}

/// <summary>
/// 背景资源库：导入过的主题包和图片都存在本地，除非用户主动删，否则一直留着.
/// 索引用手写的竖线文本，避免 System.Text.Json 在裁剪下的反射问题.
/// </summary>
public static class BackgroundLibrary
{
    private const string TransparentId = "__transparent__";
    private const string CurrentKey = "CurrentBackgroundId";

    private static string Root
        => Path.Combine(ApplicationData.Current.LocalFolder.Path, "Backgrounds");

    private static string IndexFile => Path.Combine(Root, "index.txt");

    /// <summary>
    /// 当前选中的背景 id，默认透明.
    /// </summary>
    public static string CurrentId
    {
        get => ApplicationData.Current.LocalSettings.Values.TryGetValue(CurrentKey, out var value) && value is string id
            ? id
            : TransparentId;
        set => ApplicationData.Current.LocalSettings.Values[CurrentKey] = value;
    }

    /// <summary>
    /// 读出全部背景，第一项永远是「透明」.
    /// </summary>
    public static List<BackgroundEntry> Load()
    {
        Directory.CreateDirectory(Root);
        var list = new List<BackgroundEntry>
        {
            new() { Id = TransparentId, Name = "透明", IsTransparent = true },
        };

        if (!File.Exists(IndexFile))
        {
            return list;
        }

        foreach (var line in File.ReadAllLines(IndexFile))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length < 4)
            {
                continue;
            }

            var video = string.IsNullOrEmpty(parts[2]) ? null : parts[2];
            var image = string.IsNullOrEmpty(parts[3]) ? null : parts[3];
            var preview = parts.Length > 4 && !string.IsNullOrEmpty(parts[4]) && File.Exists(parts[4]) ? parts[4] : null;
            preview ??= image;
            if ((video is null || !File.Exists(video)) && (image is null || !File.Exists(image)))
            {
                continue;
            }

            list.Add(new BackgroundEntry
            {
                Id = parts[0],
                Name = parts[1],
                VideoPath = video,
                ImagePath = image,
                PreviewPath = preview,
            });
        }

        return list;
    }

    private static void SaveIndex(IEnumerable<BackgroundEntry> entries)
    {
        Directory.CreateDirectory(Root);
        var lines = entries
            .Where(entry => !entry.IsTransparent)
            .Select(entry => $"{entry.Id}|{entry.Name}|{entry.VideoPath}|{entry.ImagePath}|{entry.PreviewPath}");
        File.WriteAllLines(IndexFile, lines);
    }

    /// <summary>
    /// 按 id 取一条，取不到返回 null.
    /// </summary>
    public static BackgroundEntry? Find(string id)
        => Load().FirstOrDefault(entry => entry.Id == id);

    /// <summary>
    /// 当前生效的那一条.
    /// </summary>
    public static BackgroundEntry Current
    {
        get
        {
            var id = CurrentId;
            var entry = Find(id);
            if (entry is null)
            {
                // 选中的那条被删了，回落到透明.
                CurrentId = TransparentId;
                return new BackgroundEntry { Id = TransparentId, Name = "透明", IsTransparent = true };
            }

            return entry;
        }
    }

    /// <summary>
    /// 导入一张本地图片当背景.
    /// </summary>
    public static BackgroundEntry ImportImage(string sourcePath)
    {
        var id = "img_" + DateTime.Now.ToString("yyyyMMddHHmmss");
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        var fileName = Path.GetFileName(sourcePath);
        var target = Path.Combine(dir, fileName);
        File.Copy(sourcePath, target, overwrite: true);

        var entry = new BackgroundEntry
        {
            Id = id,
            Name = Path.GetFileNameWithoutExtension(sourcePath),
            ImagePath = target,
            // 用户传的图本身就是最好的预览.
            PreviewPath = target,
        };

        var list = Load();
        list.Add(entry);
        SaveIndex(list);
        return entry;
    }

    /// <summary>
    /// 导入一份 B 站主题 JSON：读 package_url，下载 zip，解出静态图和动态视频.
    /// </summary>
    public static async Task<BackgroundEntry> ImportThemeAsync(string jsonPath)
    {
        var text = await File.ReadAllTextAsync(jsonPath);
        var name = ExtractJsonString(text, "\"name\"") ?? Path.GetFileNameWithoutExtension(jsonPath);
        var packageUrl = ExtractJsonString(text, "\"package_url\"")
            ?? throw new InvalidOperationException("这份 JSON 里没有 package_url，可能不是 B 站主题文件。");

        var id = "theme_" + DateTime.Now.ToString("yyyyMMddHHmmss");
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        var zipPath = Path.Combine(dir, "package.zip");
        using (var http = new System.Net.Http.HttpClient())
        {
            http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            http.Timeout = TimeSpan.FromSeconds(60);
            var bytes = await http.GetByteArrayAsync(packageUrl);
            await File.WriteAllBytesAsync(zipPath, bytes);
        }

        var extractDir = Path.Combine(dir, "package");
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, extractDir);
        File.Delete(zipPath);

        string? video = null;
        string? cover = null;

        // 背景图有三种命名，不能靠遍历顺序碰运气，先各自收着最后按优先级挑.
        string? imageSquared = null;   // 旧格式 head_myself_squared_bg.*
        string? imageMyself = null;    // 新格式 head_myself_bg.*
        string? imageFallback = null;  // 兜底 head_bg.*
        var imageSquaredIsJpg = false;

        foreach (var file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file).ToLowerInvariant();
            if (fileName.StartsWith("head_myself_mp4_bg") && video is null)
            {
                // 动态背景，新旧格式都叫这个名字.
                video = file;
            }
            else if (fileName.StartsWith("head_myself_squared_bg"))
            {
                // 旧格式：head_myself_squared_bg.jpg / .png，优先 jpg（体积小）.
                if (imageSquared is null || (fileName.EndsWith(".jpg") && !imageSquaredIsJpg))
                {
                    imageSquared = file;
                    imageSquaredIsJpg = fileName.EndsWith(".jpg");
                }
            }
            else if (fileName.StartsWith("head_myself_bg") && imageMyself is null)
            {
                // 新格式：没有 squared，直接叫 head_myself_bg.jpg / .png.
                imageMyself = file;
            }
            else if (fileName.StartsWith("head_bg") && imageFallback is null)
            {
                // 再退一步，用全局头图兜底（优先级最低）.
                imageFallback = file;
            }
            else if ((fileName.StartsWith("image_cover") || fileName.StartsWith("image_preview")) && cover is null)
            {
                // 装扮包自带的封面，拿来做列表预览.
                cover = file;
            }
        }

        var image = imageSquared ?? imageMyself ?? imageFallback;
        if (video is null && image is null)
        {
            Directory.Delete(dir, recursive: true);
            throw new InvalidOperationException("这个主题包里没有找到背景素材。");
        }

        var entry = new BackgroundEntry
        {
            Id = id,
            Name = name,
            VideoPath = video,
            ImagePath = image,
            // 装扮包如果带了 image_cover / image_preview 就用它当预览，否则退回静态背景图.
            PreviewPath = cover ?? image,
        };

        var list = Load();
        list.Add(entry);
        SaveIndex(list);
        return entry;
    }

    /// <summary>
    /// 删掉一条背景，连它的资源目录一起删.
    /// </summary>
    public static void Remove(string id)
    {
        if (id == TransparentId)
        {
            return;
        }

        var list = Load();
        var entry = list.FirstOrDefault(item => item.Id == id);
        if (entry is null)
        {
            return;
        }

        list.Remove(entry);
        SaveIndex(list);

        TryDeleteDirectory(Path.Combine(Root, id));

        if (CurrentId == id)
        {
            CurrentId = TransparentId;
        }
    }

    /// <summary>
    /// 清掉没被任何记录引用的残留：旧版本下的 ThemeCache，以及 Backgrounds 里孤儿目录.
    /// </summary>
    public static void CleanupOrphanCache()
    {
        var legacyCache = Path.Combine(ApplicationData.Current.LocalFolder.Path, "ThemeCache");
        TryDeleteDirectory(legacyCache);

        if (!Directory.Exists(Root))
        {
            return;
        }

        var used = Load().Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (!used.Contains(name))
            {
                TryDeleteDirectory(dir);
            }
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                {
                    Serilog.Log.Warning(ex, "删除目录失败：{Dir}", dir);
                }
                else
                {
                    // 文件刚被释放时可能还占着，等一下再试.
                    System.Threading.Thread.Sleep(120);
                }
            }
        }
    }

    private static string? ExtractJsonString(string json, string key)
    {
        var index = json.IndexOf(key, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var colon = json.IndexOf(':', index);
        if (colon < 0)
        {
            return null;
        }

        var start = json.IndexOf('"', colon);
        if (start < 0)
        {
            return null;
        }

        var end = json.IndexOf('"', start + 1);
        return end < 0 ? null : json.Substring(start + 1, end - start - 1);
    }
}




