// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models;

/// <summary>
/// 累计实际播放时间，忽略暂停、缓冲和跳转产生的进度变化.
/// </summary>
public sealed class PlaybackWatchTracker
{
    private double? _position;
    private TimeSpan _timestamp;
    private bool _wasPlaying;
    private double _watchedSeconds;

    /// <summary>
    /// 记录一次播放状态，返回是否已实际观看超过一分钟.
    /// </summary>
    public bool Observe(double position, TimeSpan timestamp, bool isPlaying, double rate)
    {
        var elapsed = (timestamp - _timestamp).TotalSeconds;
        var delta = position - (_position ?? position);
        if (_wasPlaying && isPlaying && elapsed > 0 && elapsed < 5 && rate > 0
            && delta > 0 && delta <= Math.Max(2, elapsed * rate * 1.5))
        {
            _watchedSeconds += Math.Min(elapsed, delta / rate);
        }

        _position = position;
        _timestamp = timestamp;
        _wasPlaying = isPlaying;
        return _watchedSeconds > 60;
    }
}
