// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;

namespace BiliCopilot.UI.Services.Mpv;

/// <summary>
/// 这次要下发的 mpv 属性名.
/// 统一放一处：设置页、播放器、状态读取都从这里取，避免三处各写一遍字符串。
/// </summary>
public static class MpvCachePropertyNames
{
    /// <summary>前向缓存上限（字节，接受 300MiB 这种写法）.</summary>
    public const string DemuxerMaxBytes = "demuxer-max-bytes";

    /// <summary>后向缓存上限（已播放过的部分），显式 0 表示不留.</summary>
    public const string DemuxerMaxBackBytes = "demuxer-max-back-bytes";

    /// <summary>预读秒数.</summary>
    public const string DemuxerReadAheadSeconds = "demuxer-readahead-secs";

    /// <summary>缓存时长上限（秒），不设时 mpv 默认约 1000 小时.</summary>
    public const string CacheSeconds = "cache-secs";

    /// <summary>前向/后向缓存互相借用.</summary>
    public const string DemuxerDonateBuffer = "demuxer-donate-buffer";

    /// <summary>允许在已缓存范围内直接跳转.</summary>
    public const string DemuxerSeekableCache = "demuxer-seekable-cache";

    /// <summary>把缓存写到磁盘.</summary>
    public const string CacheOnDisk = "cache-on-disk";

    /// <summary>磁盘缓存目录.</summary>
    public const string DemuxerCacheDir = "demuxer-cache-dir";

    /// <summary>当前已缓冲的时长（秒，读）.</summary>
    public const string DemuxerCacheDuration = "demuxer-cache-duration";

    /// <summary>缓冲进度 0~100（读）；100 表示不在缓冲.</summary>
    public const string CacheBufferingState = "cache-buffering-state";

    /// <summary>缓冲状态明细（node 属性，由内核解析后通过事件送出）.</summary>
    public const string DemuxerCacheState = "demuxer-cache-state";
}

/// <summary>
/// 缓冲相关设置的默认值.
/// 设置页与播放器只从这里取：之前两边各写一份（设置页 300 秒、播放器 360 秒），
/// 用户没改过设置时也会出现「界面上看是 300、实际下发 360」的不一致.
/// </summary>
public static class MpvCacheDefaults
{
    /// <summary>前向缓存默认值（MiB）.</summary>
    public const double ForwardMiB = 300d;

    /// <summary>后向缓存默认值（MiB），0 表示不保留已播片段.</summary>
    public const double BackMiB = 0d;

    /// <summary>预读秒数默认值.</summary>
    public const double ReadAheadSeconds = 360d;

    /// <summary>缓存时长上限默认值，0 表示不限制、不下发 cache-secs.</summary>
    public const double CacheSecondsLimit = 0d;

    /// <summary>前后向借用默认值，与 mpv 默认一致（开）.</summary>
    public const bool DonateBuffer = true;

    /// <summary>缓存内快速跳转默认值，与 mpv 默认一致（开）.</summary>
    public const bool SeekableCache = true;

    /// <summary>磁盘缓存默认值（关）.</summary>
    public const bool CacheOnDisk = false;

    /// <summary>
    /// 「网络缓存」默认是否跟随 mpv 默认.
    /// 默认 false（= 按下面的用户值下发），因为这是改动之前的行为，不顺手改用户的实际缓冲量.
    /// </summary>
    public const bool ForwardAuto = false;
}

/// <summary>
/// 一次初始化用了哪份 mpv 配置.
/// 只影响日志标签：不管用内置设置还是自定义 mpv.conf，缓冲参数都会下发.
/// </summary>
public enum MpvCacheConfigSource
{
    /// <summary>用应用里的内置设置（没有自定义 mpv.conf）.</summary>
    BuiltIn,

    /// <summary>用用户自己的 mpv.conf；缓冲参数仍然会下发，同名项以这里下发的为准.</summary>
    CustomMpvConf,
}

/// <summary>
/// 一个缓冲参数的三态取值：
/// <list type="bullet">
/// <item><description>自动（<see cref="IsAuto"/>）：跟随 mpv 默认，<b>不下发</b>；</description></item>
/// <item><description>显式 0：下发 "0"，用来真正关掉这一项；</description></item>
/// <item><description>用户值：下发用户填的数值.</description></item>
/// </list>
/// 旧实现把「0」当成「没设置」直接跳过，等于用户关掉后向缓存也仍在用 mpv 默认的 50MiB，
/// 这就是「显式 0 不生效」的根因，所以这里必须把三者分开.
/// </summary>
public readonly record struct MpvCacheOptionValue
{
    private MpvCacheOptionValue(bool isAuto, string? text)
    {
        IsAuto = isAuto;
        Text = text;
    }

    /// <summary>是否跟随 mpv 默认（true = 不下发）.</summary>
    public bool IsAuto { get; }

    /// <summary>要下发的原始文本；<see cref="IsAuto"/> 为 true 时是 null.</summary>
    public string? Text { get; }

    /// <summary>自动：不下发，交给 mpv.</summary>
    public static MpvCacheOptionValue Auto { get; } = new(true, null);

    /// <summary>按 MiB 生成字节数文本（mpv 接受 300MiB 这种后缀）.</summary>
    /// <param name="isAuto">是否自动.</param>
    /// <param name="mib">兆字节数，允许 0.</param>
    /// <returns>三态取值.</returns>
    public static MpvCacheOptionValue FromMiB(bool isAuto, double mib)
        => isAuto ? Auto : new MpvCacheOptionValue(false, FormatNumber(mib) + "MiB");

    /// <summary>按普通数值生成文本（秒等）.</summary>
    /// <param name="isAuto">是否自动.</param>
    /// <param name="value">数值，允许 0.</param>
    /// <returns>三态取值.</returns>
    public static MpvCacheOptionValue FromNumber(bool isAuto, double value)
        => isAuto ? Auto : new MpvCacheOptionValue(false, FormatNumber(value));

    /// <summary>直接给文本（yes/no 这种开关值、测试里的特殊值）.</summary>
    /// <param name="text">要下发的文本.</param>
    /// <returns>三态取值.</returns>
    public static MpvCacheOptionValue FromText(string text) => new(false, text);

    /// <summary>用整数文本下发，避免出现 300.0 这种.</summary>
    private static string FormatNumber(double value)
        => Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 一次初始化用的缓冲设置快照.
/// 由 <see cref="MpvCacheSettingsFactory"/> 从本地设置读出来；播放器不直接读 SettingNames.
/// </summary>
public sealed record MpvCacheSettings
{
    /// <summary>前向缓存（demuxer-max-bytes）.</summary>
    public MpvCacheOptionValue ForwardCache { get; init; } = MpvCacheOptionValue.FromMiB(MpvCacheDefaults.ForwardAuto, MpvCacheDefaults.ForwardMiB);

    /// <summary>后向缓存（demuxer-max-back-bytes）.</summary>
    public MpvCacheOptionValue BackCache { get; init; } = MpvCacheOptionValue.FromMiB(false, MpvCacheDefaults.BackMiB);

    /// <summary>预读秒数（demuxer-readahead-secs）.</summary>
    public MpvCacheOptionValue ReadAheadSeconds { get; init; } = MpvCacheOptionValue.FromNumber(MpvCacheDefaults.ForwardAuto, MpvCacheDefaults.ReadAheadSeconds);

    /// <summary>缓存时长上限（cache-secs）.</summary>
    public MpvCacheOptionValue CacheSeconds { get; init; } = MpvCacheOptionValue.Auto;

    /// <summary>前后向借用（demuxer-donate-buffer）.</summary>
    public MpvCacheOptionValue DonateBuffer { get; init; } = MpvCacheOptionValue.FromText(MpvCacheDefaults.DonateBuffer ? "yes" : "no");

    /// <summary>缓存内快速跳转（demuxer-seekable-cache）.</summary>
    public MpvCacheOptionValue SeekableCache { get; init; } = MpvCacheOptionValue.FromText(MpvCacheDefaults.SeekableCache ? "yes" : "no");

    /// <summary>是否写磁盘缓存.</summary>
    public bool CacheOnDisk { get; init; } = MpvCacheDefaults.CacheOnDisk;

    /// <summary>磁盘缓存目录；为空表示交给 mpv 自己选.</summary>
    public string? CacheDir { get; init; }

    /// <summary>是不是直播（直播不写磁盘缓存）.</summary>
    public bool IsLive { get; init; }

    /// <summary>本次用的是内置设置还是自定义 mpv.conf，只用于日志.</summary>
    public MpvCacheConfigSource ConfigSource { get; init; } = MpvCacheConfigSource.BuiltIn;

    /// <summary>
    /// 造一份全默认的设置.
    /// 单元测试与「恢复默认」都走这里，保证默认值只有一处定义.
    /// </summary>
    /// <param name="isLive">是否直播.</param>
    /// <param name="configSource">配置来源.</param>
    /// <returns>设置快照.</returns>
    public static MpvCacheSettings CreateDefault(bool isLive = false, MpvCacheConfigSource configSource = MpvCacheConfigSource.BuiltIn)
        => new() { IsLive = isLive, ConfigSource = configSource };
}

/// <summary>一条要下发给 mpv 的属性.</summary>
/// <param name="Property">属性名.</param>
/// <param name="Value">要下发的文本.</param>
public sealed record MpvCacheOption(string Property, string Value);

/// <summary>
/// 一次下发计划：真正要下发的属性，以及因为选了「自动」而跳过的属性（后者只写日志）.
/// </summary>
/// <param name="Options">要下发的属性，顺序即下发顺序.</param>
/// <param name="SkippedAuto">因「自动」跳过、不下发的属性名.</param>
public sealed record MpvCachePlan(IReadOnlyList<MpvCacheOption> Options, IReadOnlyList<string> SkippedAuto);

/// <summary>
/// 把缓冲设置翻成 mpv 属性动作.
/// 纯函数、不碰 mpv、不碰 UI，方便单测（测试工程直接编译这个文件）.
/// </summary>
public static class MpvCacheOptionPlanner
{
    /// <summary>
    /// 生成下发计划.
    /// 注意：这里<b>不看</b> <see cref="MpvCacheSettings.ConfigSource"/> ——
    /// 自定义 mpv.conf 也照样下发，顺序上在 SetConfigFileAsync 之后，同名项以这里为准.
    /// </summary>
    /// <param name="settings">设置快照.</param>
    /// <returns>下发计划.</returns>
    public static MpvCachePlan Build(MpvCacheSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var options = new List<MpvCacheOption>(8);
        var skipped = new List<string>(4);

        Add(options, skipped, MpvCachePropertyNames.DemuxerMaxBytes, settings.ForwardCache);
        Add(options, skipped, MpvCachePropertyNames.DemuxerMaxBackBytes, settings.BackCache);
        Add(options, skipped, MpvCachePropertyNames.DemuxerReadAheadSeconds, settings.ReadAheadSeconds);
        Add(options, skipped, MpvCachePropertyNames.CacheSeconds, settings.CacheSeconds);
        Add(options, skipped, MpvCachePropertyNames.DemuxerDonateBuffer, settings.DonateBuffer);
        Add(options, skipped, MpvCachePropertyNames.DemuxerSeekableCache, settings.SeekableCache);

        // 直播不写磁盘缓存：mpv 对直播流建文件缓存会失败（日志里 Failed to create file cache），
        // 失败后偶尔连视频轨都起不来. 这条规则从旧实现搬过来，别丢.
        var useCacheOnDisk = settings.CacheOnDisk && !settings.IsLive;
        options.Add(new MpvCacheOption(MpvCachePropertyNames.CacheOnDisk, useCacheOnDisk ? "yes" : "no"));
        if (useCacheOnDisk && !string.IsNullOrWhiteSpace(settings.CacheDir))
        {
            options.Add(new MpvCacheOption(MpvCachePropertyNames.DemuxerCacheDir, settings.CacheDir!));
        }

        return new MpvCachePlan(options, skipped);
    }

    private static void Add(List<MpvCacheOption> options, List<string> skipped, string property, MpvCacheOptionValue value)
    {
        if (value.IsAuto || value.Text is null)
        {
            skipped.Add(property);
            return;
        }

        options.Add(new MpvCacheOption(property, value.Text));
    }
}
