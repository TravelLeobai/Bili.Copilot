// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;

namespace BiliCopilot.UI.Services.Mpv;

/// <summary>
/// 把本地设置读成一份 <see cref="MpvCacheSettings"/>.
/// 播放器只调这里，不直接认 SettingNames，省得「设置页改了键名、播放器还在读旧键」.
/// </summary>
public static class MpvCacheSettingsFactory
{
    /// <summary>
    /// 读当前设置.
    /// </summary>
    /// <param name="isLive">是不是直播.</param>
    /// <param name="configSource">本次用的是内置设置还是自定义 mpv.conf（只影响日志）.</param>
    /// <returns>设置快照.</returns>
    public static MpvCacheSettings Create(bool isLive, MpvCacheConfigSource configSource = MpvCacheConfigSource.BuiltIn)
    {
        // 「网络缓存」选了自动 = 前向缓存和预读都不下发，完全交给 mpv 自己的默认值.
        var forwardAuto = SettingsToolkit.ReadLocalSetting(SettingNames.CacheAutoMode, MpvCacheDefaults.ForwardAuto);
        var forwardMiB = SettingsToolkit.ReadLocalSetting(SettingNames.MaxCacheSize, MpvCacheDefaults.ForwardMiB);
        var backMiB = SettingsToolkit.ReadLocalSetting(SettingNames.MaxBackCacheSize, MpvCacheDefaults.BackMiB);
        var readAheadSeconds = SettingsToolkit.ReadLocalSetting(SettingNames.MaxCacheSeconds, MpvCacheDefaults.ReadAheadSeconds);
        var cacheSecondsLimit = SettingsToolkit.ReadLocalSetting(SettingNames.CacheSecsLimit, MpvCacheDefaults.CacheSecondsLimit);
        var donateBuffer = SettingsToolkit.ReadLocalSetting(SettingNames.DemuxerDonateBuffer, MpvCacheDefaults.DonateBuffer);
        var seekableCache = SettingsToolkit.ReadLocalSetting(SettingNames.DemuxerSeekableCache, MpvCacheDefaults.SeekableCache);
        var cacheOnDisk = SettingsToolkit.ReadLocalSetting(SettingNames.CacheOnDisk, MpvCacheDefaults.CacheOnDisk);
        var cacheDir = SettingsToolkit.ReadLocalSetting(SettingNames.CacheDir, string.Empty);

        // 目录不存在时不下发：mpv 会自己报 Failed to create file cache，和直播那条坑是同一类问题.
        var usableCacheDir = !string.IsNullOrWhiteSpace(cacheDir) && Directory.Exists(cacheDir);

        return new MpvCacheSettings
        {
            ForwardCache = MpvCacheOptionValue.FromMiB(forwardAuto, forwardMiB),
            BackCache = MpvCacheOptionValue.FromMiB(false, backMiB),
            ReadAheadSeconds = MpvCacheOptionValue.FromNumber(forwardAuto, readAheadSeconds),
            // 0 = 用户没设上限，不下发 cache-secs（mpv 默认约 1000 小时，等于不限）.
            CacheSeconds = cacheSecondsLimit > 0
                ? MpvCacheOptionValue.FromNumber(false, cacheSecondsLimit)
                : MpvCacheOptionValue.Auto,
            DonateBuffer = MpvCacheOptionValue.FromText(donateBuffer ? "yes" : "no"),
            SeekableCache = MpvCacheOptionValue.FromText(seekableCache ? "yes" : "no"),
            CacheOnDisk = cacheOnDisk,
            CacheDir = usableCacheDir ? cacheDir : null,
            IsLive = isLive,
            ConfigSource = configSource,
        };
    }
}
