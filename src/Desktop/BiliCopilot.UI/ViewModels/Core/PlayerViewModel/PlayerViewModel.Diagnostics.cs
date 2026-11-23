// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Threading;
#if DEBUG
using BiliCopilot.UI.Services.Diagnostics;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel.Core.Enums;
#endif

namespace BiliCopilot.UI.ViewModels.Core;

/// <summary>
/// 播放器视图模型 - 诊断会话.
/// <para>
/// 这里只放「会话 id」和 Debug 下的停滞监视器接线。诊断功能必须满足两条硬约束：
/// 一、不新增任何 mpv 查询（本仓内核没有同步属性读取，位置/状态本来就是推上来的）；
/// 二、Release 里连类型都不存在（<c>#if DEBUG</c>），不建线程、不产生 dump.
/// </para>
/// <para>
/// 会话 id 是 Release 也有的：弹幕重建日志（不做 Debug 隔离）需要它才能和停滞对照.
/// </para>
/// </summary>
public sealed partial class PlayerViewModel
{
    private int _diagnosticsSessionSequence;
    private string _diagnosticsSessionId = string.Empty;

    /// <summary>
    /// Gets 当前播放会话 id. 停滞日志与弹幕重建日志都带它，用来把两边的记录对上.
    /// </summary>
    internal string DiagnosticsSessionId => _diagnosticsSessionId;

    /// <summary>
    /// 开一个新的播放会话：换视频、重载、重建播放器都会走到这里.
    /// 新会话一定换新 id，旧会话的监视器不会把记录写到新会话名下.
    /// </summary>
    private void BeginPlaybackDiagnosticsSession()
    {
        var sequence = Interlocked.Increment(ref _diagnosticsSessionSequence).ToString(CultureInfo.InvariantCulture);
        _diagnosticsSessionId = string.Create(
            CultureInfo.InvariantCulture,
            $"s{LifecycleId}-{sequence}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}");

#if DEBUG
        StopPlaybackStallWatch();
        StartPlaybackStallWatch();
#endif
    }

#if DEBUG
    private const string StallDumpFolderName = "Diagnostics";

    private PlaybackStallWatch? _stallWatch;

    /// <summary>
    /// 结束当前播放会话的监视.
    /// </summary>
    private void EndPlaybackDiagnosticsSession() => StopPlaybackStallWatch();

    /// <summary>
    /// 清空停滞 dump 目录.
    /// </summary>
    /// <returns>删掉的文件数.</returns>
    internal static int CleanupPlaybackStallDumps()
        => PlaybackStallDump.Cleanup(GetPlaybackStallDumpDirectory());

    /// <summary>
    /// Gets 停滞 dump 目录：<c>LocalFolder\Diagnostics\StallDumps</c>，不落下载目录、不落网络盘.
    /// </summary>
    internal static string GetPlaybackStallDumpDirectory()
    {
        try
        {
            var local = Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path;
            return Path.Combine(local, StallDumpFolderName, PlaybackStallWatch.DirectoryFolderName);
        }
        catch (Exception ex)
        {
            // 拿不到 LocalFolder（进程被拆到一半等）：落到临时目录，也不落下载目录.
            System.Diagnostics.Debug.WriteLine(ex.Message);
            return Path.Combine(Path.GetTempPath(), "BiliCopilot", StallDumpFolderName, PlaybackStallWatch.DirectoryFolderName);
        }
    }

    private void StartPlaybackStallWatch()
    {
        try
        {
            var watch = new PlaybackStallWatch(
                GetPlaybackStallDumpDirectory(),
                _diagnosticsSessionId,
                new SerilogPlaybackStallLog());
            _stallWatch = watch;
            watch.Start();
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "STALL | watch-started session={Session} dir={Directory}",
                    _diagnosticsSessionId,
                    watch.Directory);
            }
        }
        catch (Exception ex)
        {
            // 诊断起不来绝不能影响播放.
            _stallWatch = null;
            logger.LogWarning("STALL | watch-start-failed session={Session} error={Error}", _diagnosticsSessionId, ex.GetType().Name);
        }
    }

    private void StopPlaybackStallWatch()
    {
        var watch = _stallWatch;
        if (watch is null)
        {
            return;
        }

        _stallWatch = null;

        // 只置位，不 Join：停止是异步的，UI 线程不在这里等.
        // 线程退出前会自己写最后一行 watch-stopped.
        // 也不 await Completion：切视频/Dispose 都在 UI 线程上，等它就等于 Join.
        // 线程只读不可变快照、不碰播放器对象，多活一拍也不会摸到已经释放的 mpv.
        watch.Dispose();
    }

    /// <summary>
    /// 从既有的属性变更回调喂一次观察（不新增任何 mpv 查询）.
    /// </summary>
    /// <param name="propertyName">变了哪个属性.</param>
    private void ObservePlaybackForStall(string? propertyName)
    {
        var watch = _stallWatch;
        var player = Player;
        if (watch is null || player is null)
        {
            return;
        }

        try
        {
            if (string.Equals(propertyName, nameof(Player.Position), StringComparison.Ordinal))
            {
                watch.Observe(IsPlayingState(player.PlaybackState), player.Position);
            }
            else if (string.Equals(propertyName, nameof(Player.PlaybackState), StringComparison.Ordinal))
            {
                // 状态切换（暂停 / 拖动 / 缓冲 / 加载 / 停）都算「会话在稳定中」：这段时间的停滞不采.
                watch.NotifySettling();
                watch.Observe(IsPlayingState(player.PlaybackState), player.Position);
            }
        }
        catch (Exception ex)
        {
            // 播放器正在换实例：读属性可能撞上空引用，诊断安静跳过（但不要在 catch 里写日志，日志可能正忙）.
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
    }

    /// <summary>
    /// 缓冲状态变化（欠载 / 恢复）也算稳定期.
    /// </summary>
    private void NotifyPlaybackStallSettling()
        => _stallWatch?.NotifySettling();

    private static bool IsPlayingState(MpvPlayerState state) => state == MpvPlayerState.Playing;

    /// <summary>
    /// 一次状态读取 + 消费：进度通知落到弹幕渲染层这一段. 读得慢就是「读取停滞」.
    /// </summary>
    /// <param name="position">当前播放位置（秒）.</param>
    private void UpdateDanmakuPositionForStall(double position)
    {
        var watch = _stallWatch;
        if (watch is null)
        {
            Danmaku.UpdatePosition(Convert.ToInt32(position));
            return;
        }

        watch.BeginRead();
        try
        {
            Danmaku.UpdatePosition(Convert.ToInt32(position));
        }
        finally
        {
            watch.EndRead();
        }
    }

    /// <summary>
    /// 把 Serilog 接到停滞诊断上.
    /// </summary>
    private sealed class SerilogPlaybackStallLog : IPlaybackStallLog
    {
        /// <inheritdoc/>
        public bool IsInformationEnabled => Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Information);

        /// <inheritdoc/>
        public void Information(string messageTemplate, params object?[] values)
            => Serilog.Log.Information(messageTemplate, values);

        /// <inheritdoc/>
        public void Warning(string messageTemplate, params object?[] values)
            => Serilog.Log.Warning(messageTemplate, values);
    }
#endif
}
