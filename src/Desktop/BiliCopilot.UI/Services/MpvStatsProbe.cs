// Copyright (c) Bili Copilot. All rights reserved.

using System.Text;
using System.Text.Json;

namespace BiliCopilot.UI.Services;

/// <summary>
/// mpv 统计探针.
/// 内核没有暴露任意属性读取，所以挂一个 Lua 脚本，把 video-bitrate / audio-bitrate /
/// frame-drop-count 每秒写进一个 json 文件，C# 侧定时读回来.
/// </summary>
internal static class MpvStatsProbe
{
    private const string ScriptFileName = "bili-stats-probe.lua";

    private static bool _loaded;

    /// <summary>
    /// 统计文件的完整路径.
    /// </summary>
    public static string DataFilePath { get; } = Path.Combine(
        Windows.Storage.ApplicationData.Current.LocalFolder.Path,
        "mpv-stats.json");

    /// <summary>
    /// 把 Lua 脚本写到本地目录（内容内嵌在程序里，省得处理资源打包），返回脚本路径.
    /// </summary>
    public static string EnsureScript()
    {
        var path = Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
            ScriptFileName);

        // Lua 里路径要转义，Windows 反斜杠在 Lua 字符串里是转义符.
        // 这里不用插值字符串：Lua 的 string.format 里全是花括号，用占位符替换更省事.
        var luaPath = DataFilePath.Replace("\\", "\\\\");
        var script = """
            -- 由哔哩助理写入，用途：把 mpv 的实时统计值导出成 json 供客户端读取.
            local out = "__OUT__"

            local function num(name)
              local v = mp.get_property_number(name)
              return v == nil and -1 or v
            end

            local function tick()
              local ok, err = pcall(function()
                local f = io.open(out, "w")
                if not f then
                  return
                end
                f:write(string.format(
                  '{"video_bitrate":%.0f,"audio_bitrate":%.0f,"dropped":%.0f,"fps":%.2f}',
                  num("video-bitrate"), num("audio-bitrate"), num("frame-drop-count"), num("container-fps")))
                f:close()
              end)
              if not ok then
                mp.msg.warn("bili stats probe: " .. tostring(err))
              end
            end

            mp.add_periodic_timer(1.0, tick)
            tick()
            """.Replace("__OUT__", luaPath);

        try
        {
            File.WriteAllText(path, script, new UTF8Encoding(false));
        }
        catch
        {
            // 写不了就让调用方在读的时候自然失败.
        }

        return path;
    }

    /// <summary>
    /// 确保脚本已经挂到当前播放器上（每个客户端只挂一次）.
    /// </summary>
    /// <param name="client">mpv 客户端.</param>
    public static async Task<bool> EnsureLoadedAsync(Richasy.MpvKernel.Core.MpvClient? client)
    {
        if (client is null)
        {
            return false;
        }

        if (_loaded)
        {
            return true;
        }

        try
        {
            await client.LoadScriptAsync(EnsureScript());
            _loaded = true;
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "挂载 mpv 统计脚本失败");
            return false;
        }
    }

    /// <summary>
    /// 读回脚本写出的统计值；文件不存在或太旧就返回 null.
    /// </summary>
    public static (double VideoBitrate, double AudioBitrate, double Dropped, double Fps)? Read()
    {
        try
        {
            if (!File.Exists(DataFilePath))
            {
                return null;
            }

            // 脚本每秒写一次，超过 5 秒没更新说明播放器停了，别拿旧数字糊弄.
            var age = DateTime.Now - File.GetLastWriteTime(DataFilePath);
            if (age > TimeSpan.FromSeconds(5))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(DataFilePath));
            var root = document.RootElement;
            return (
                ReadDouble(root, "video_bitrate"),
                ReadDouble(root, "audio_bitrate"),
                ReadDouble(root, "dropped"),
                ReadDouble(root, "fps"));
        }
        catch
        {
            return null;
        }
    }

    private static double ReadDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number
            ? node.GetDouble()
            : -1;
}
