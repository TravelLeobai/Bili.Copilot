// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services.Sponsor;

/// <summary>
/// 空降助手的「片段静音」状态（actionType = mute 的那几段）.
/// <para>
/// 为什么要有这个类型：静音是「压音量 + 记住原音量」两件事组成的状态，
/// 原来散在 <c>PlayerViewModel</c> 的三个字段里（<c>_sponsorMuteApplied</c> /
/// <c>_sponsorMuteBackupVolume</c>），换集时只清了片段表、没清这两个，
/// 于是上一集停在 mute 段时下一集带着「已静音」进来 ——
/// <c>MuteForSponsoredSegment</c> 看到标记直接 return，音量再也不压、也不恢复，整集没声音.
/// 现在状态只有一个入口能改，换集的复位（<see cref="ResetForNewMedia"/>）变成必答题，
/// 顺手也能单测.
/// </para>
/// <para>
/// 线程模型：只从播放器 tick（UI 线程）调用，不做额外加锁.
/// </para>
/// </summary>
public sealed class SponsorMuteState
{
    /// <summary>
    /// 音量默认值（mpv 的音量范围是 0–100，设置项默认也是 100）.
    /// </summary>
    public const double DefaultVolume = 100d;

    /// <summary>
    /// 当前是否处在「被片段压过音量」的状态.
    /// </summary>
    public bool IsMuted { get; private set; }

    /// <summary>
    /// 压音量前的原音量；未静音时是 <see cref="DefaultVolume"/>.
    /// </summary>
    public double BackupVolume { get; private set; } = DefaultVolume;

    /// <summary>
    /// 进入 mute 片段：记住原音量.
    /// </summary>
    /// <param name="currentVolume">当前音量（压之前的）.</param>
    /// <returns>true 表示这次真的从「未静音」变成「已静音」，调用方应该把音量压到 0；
    /// false 表示本来就已经静音了（重复 tick），不要再压一次、也不要覆盖原音量.</returns>
    public bool Mute(double currentVolume)
    {
        if (IsMuted)
        {
            return false;
        }

        IsMuted = true;
        BackupVolume = Sanitize(currentVolume);
        return true;
    }

    /// <summary>
    /// 离开 mute 片段：取回原音量.
    /// </summary>
    /// <param name="volume">要恢复的音量.</param>
    /// <returns>true 表示这次真的从「已静音」恢复，调用方应该把音量设回去；
    /// false 表示本来就没静音（不要无端改用户音量）.</returns>
    public bool TryRestore(out double volume)
    {
        volume = 0;
        if (!IsMuted)
        {
            return false;
        }

        volume = BackupVolume;
        IsMuted = false;
        BackupVolume = DefaultVolume;
        return true;
    }

    /// <summary>
    /// 换集 / 换视频 / 重新初始化时的复位.
    /// </summary>
    /// <returns>需要还给播放器的音量；没有静音过则返回 null（调用方什么都不用做）.</returns>
    public double? ResetForNewMedia()
    {
        if (!IsMuted)
        {
            BackupVolume = DefaultVolume;
            return null;
        }

        var volume = BackupVolume;
        IsMuted = false;
        BackupVolume = DefaultVolume;
        return volume;
    }

    private static double Sanitize(double volume)
    {
        // 拿到的音量可能是 NaN（属性还没就绪）或负数；这两种都按默认音量记，
        // 免得恢复时把一个坏值写回播放器.
        if (double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0)
        {
            return DefaultVolume;
        }

        return volume > 200 ? DefaultVolume : volume;
    }
}
