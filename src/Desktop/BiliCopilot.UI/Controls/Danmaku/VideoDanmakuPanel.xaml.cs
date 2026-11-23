// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Diagnostics;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Richasy.BiliKernel.Models;
using Richasy.Danmaku;
using Richasy.Danmaku.Enums;
using Richasy.Danmaku.Legacy;
using Richasy.Danmaku.Models;
using System.ComponentModel;

namespace BiliCopilot.UI.Controls.Danmaku;

/// <summary>
/// 视频弹幕面板.
/// </summary>
public sealed partial class VideoDanmakuPanel : DanmakuControlBase
{
    private int _lastProgress;
    private DanmakuFrostMaster? _danmakuMaster;
    private DanmakuRenderer? _danmakuRenderer;

    // 重建代号：只用于判断一份计时结果有没有过期，不参与渲染决策.
    private readonly DanmakuRebuildGeneration _rebuildGeneration = new();

    public VideoDanmakuPanel() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        if (ViewModel is null)
        {
            return;
        }

        // 计时埋点：弹幕面板首次加载（渲染器/图层创建）耗时.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ReloadRenderer();
        ResetDanmakuStyle();
        stopwatch.Stop();
        Serilog.Log.Information(
            "计时[{Group}] 视频弹幕面板首次加载：{Elapsed:F2} ms",
            App.DeferContextFlyoutEnabled ? "defer" : "baseline",
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private void ReloadRenderer()
    {
        var type = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuRenderer, DanmakuRendererType.Win2D);
        if (type == DanmakuRendererType.DirectX && _danmakuRenderer == null)
        {
            _danmakuRenderer = new DanmakuRenderer(RootGrid, this.Get<ILogger<DanmakuRenderer>>());
        }
        else if (type == DanmakuRendererType.Win2D && _danmakuMaster == null)
        {
            _danmakuMaster = new DanmakuFrostMaster(RootGrid);
        }
    }

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        if (ViewModel is not null)
        {
            ViewModel.RequestClearDanmaku -= OnRequestClearDanmaku;
            ViewModel.ProgressChanged -= OnProgressChanged;
            ViewModel.PauseDanmaku -= OnPauseDanmaku;
            ViewModel.ResumeDanmaku -= OnResumeDanmaku;
            ViewModel.RequestResetStyle -= OnRequestResetStyle;
            ViewModel.ExtraSpeedChanged -= OnExtraSpeedChanged;
            ViewModel.RequestRedrawDanmaku -= OnRedrawDanmaku;
            ViewModel.RequestAddSingleDanmaku -= OnRequestAddSingleDanmaku;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _danmakuMaster?.Close();
        _danmakuRenderer?.Dispose();
    }

    /// <inheritdoc/>
    protected override void OnViewModelChanged(DanmakuRenderViewModel? oldValue, DanmakuRenderViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.RequestClearDanmaku -= OnRequestClearDanmaku;
            oldValue.ProgressChanged -= OnProgressChanged;
            oldValue.PauseDanmaku -= OnPauseDanmaku;
            oldValue.ResumeDanmaku -= OnResumeDanmaku;
            oldValue.RequestResetStyle -= OnRequestResetStyle;
            oldValue.ExtraSpeedChanged -= OnExtraSpeedChanged;
            oldValue.RequestRedrawDanmaku -= OnRedrawDanmaku;
            oldValue.RequestAddSingleDanmaku -= OnRequestAddSingleDanmaku;
            oldValue.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (newValue is null)
        {
            return;
        }

        newValue.RequestClearDanmaku += OnRequestClearDanmaku;
        newValue.ProgressChanged += OnProgressChanged;
        newValue.PauseDanmaku += OnPauseDanmaku;
        newValue.ResumeDanmaku += OnResumeDanmaku;
        newValue.RequestResetStyle += OnRequestResetStyle;
        newValue.ExtraSpeedChanged += OnExtraSpeedChanged;
        newValue.RequestRedrawDanmaku += OnRedrawDanmaku;
        newValue.RequestAddSingleDanmaku += OnRequestAddSingleDanmaku;
        newValue.PropertyChanged += OnViewModelPropertyChanged;

        // 延迟执行样式重置,避免在 Layout 期间触发重入
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsLoaded && ViewModel == newValue)
            {
                ResetDanmakuStyle();
            }
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Renderer))
        {
            // 在 UI 线程上异步执行渲染器切换,避免重入问题
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!IsLoaded || ViewModel is null)
                {
                    return;
                }

                if (ViewModel.Renderer == DanmakuRendererType.DirectX && _danmakuMaster != null)
                {
                    _danmakuMaster.Close();
                    _danmakuMaster = null;
                    RootGrid.Children.Clear();
                    ReloadRenderer();
                    ResetDanmakuStyle();
                    ViewModel.Redraw("renderer");
                }
                else if (ViewModel.Renderer == DanmakuRendererType.Win2D && _danmakuRenderer != null)
                {
                    _danmakuRenderer.Dispose();
                    _danmakuRenderer = null;
                    RootGrid.Children.Clear();
                    ReloadRenderer();
                    ResetDanmakuStyle();
                    ViewModel.Redraw("renderer");
                }
            });
        }
    }

    private void OnRequestAddSingleDanmaku(object? sender, string e)
    {
        var location = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuLocation, DanmakuLocation.Scroll);
        var color = AppToolkit.HexToColor(SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.DanmakuColor, Microsoft.UI.Colors.White.ToString()));
        var isStandardSize = SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.IsDanmakuStandardSize, true);
        var model = new DanmakuItem
        {
            StartMs = Convert.ToUInt32(_lastProgress * 1000),
            Mode = (DanmakuMode)((int)location),
            TextColor = color,
            BaseFontSize = isStandardSize ? 20 : 24,
            Text = e,
            HasOutline = true,
            HasBorder = true
        };

        _danmakuMaster?.AddRealtimeDanmaku(model, true);
        _danmakuRenderer?.AddRealtimeDanmaku(model, true);
    }

    private void OnRequestClearDanmaku(object? sender, EventArgs e)
    {
        if (ViewModel.IsShowDanmaku)
        {
            _lastProgress = 0;
        }

        _danmakuMaster?.Clear();
        _danmakuRenderer?.Clear();
    }

    private void OnRequestResetStyle(object? sender, EventArgs e)
        => ResetDanmakuStyle();

    private void OnExtraSpeedChanged(object? sender, EventArgs e)
        => ResetSpeed();

    private void OnProgressChanged(object? sender, int e)
    {
        _lastProgress = e;
        UpdateDanmakuTime(e);
    }

    private void OnResumeDanmaku(object? sender, EventArgs e)
    {
        _danmakuRenderer?.Resume();
        _danmakuMaster?.Resume();
    }

    private void OnPauseDanmaku(object? sender, EventArgs e)
    {
        _danmakuRenderer?.Pause();
        _danmakuMaster?.Pause();
    }

    private void OnRedrawDanmaku(object? sender, EventArgs e)
        => Redraw();

    private void ResetDanmakuStyle()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!ViewModel.IsShowDanmaku)
        {
            _danmakuRenderer?.Pause();
            _danmakuMaster?.Pause();
        }

        if (_danmakuMaster != null)
        {
            _danmakuMaster.SetRollingDensity(-1);
            _danmakuMaster.SetOpacity(ViewModel.DanmakuOpacity);
            _danmakuMaster.SetBorderColor(Colors.Gray);
            _danmakuMaster.SetRollingAreaRatio(Convert.ToInt32(ViewModel.DanmakuArea * 10));
            _danmakuMaster.SetDanmakuFontSizeOffset(ViewModel.DanmakuFontSize);
            _danmakuMaster.SetFontFamilyName(ViewModel.DanmakuFontFamily);
            _danmakuMaster.SetIsTextBold(ViewModel.IsDanmakuBold);
            _danmakuMaster.SetDanmakuEnabledType(ViewModel.IsRollingEnabled, ViewModel.IsTopEnabled, ViewModel.IsBottomEnabled);
            _danmakuMaster.SetOutlineSize(ViewModel.OutlineSize);
        }

        if (_danmakuRenderer != null)
        {
            var config = DanmakuConfiguration.CreateDefault();
            config.TextOpacity = ViewModel.DanmakuOpacity;
            config.FontSizeScale = ViewModel.DanmakuFontSize;
            config.RollingAreaRatio = (float)ViewModel.DanmakuArea;
            config.FontFamilyName = ViewModel.DanmakuFontFamily ?? "Segoe UI";
            config.IsTextBold = ViewModel.IsDanmakuBold;
            config.OutlineSize = ViewModel.OutlineSize;

            _danmakuRenderer.SetDanmakuEnabledType(ViewModel.IsRollingEnabled, ViewModel.IsTopEnabled, ViewModel.IsBottomEnabled);
            _danmakuRenderer.UpdateConfiguration(config);
        }

        ResetSpeed();
    }

    private void ResetSpeed()
    {
        var finalSpeed = ViewModel.DanmakuSpeed * 5 * ViewModel.ExtraSpeed;
        _danmakuMaster?.SetRollingSpeed(finalSpeed);
        _danmakuRenderer?.UpdateConfiguration(new DanmakuConfiguration { RollingSpeedBase = (float)finalSpeed });
    }

    private async void Redraw()
    {
        if (!IsLoaded || ViewModel is null)
        {
            return;
        }

        // 诊断：端到端从这里起算（含下面的 500ms 延迟与 UI 队列排队）.
        var requestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var reason = ViewModel.LastRebuildReason;
        var sessionId = ViewModel.DiagnosticsSessionId;
        var generation = _rebuildGeneration.Begin();

        if (_danmakuRenderer == null || _danmakuMaster == null)
        {
            await Task.Delay(500);
        }

        // 确保在 UI 线程空闲时执行弹幕绘制
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            if (!IsLoaded || ViewModel is null)
            {
                return;
            }

            var timings = new DanmakuRebuildTimings();
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var succeeded = false;

            // 注意：这里不 catch —— 渲染层抛异常的行为必须和改动前一致（仍然往上抛）.
            try
            {
                if (ViewModel.GetCachedDanmakus().Count > 0)
                {
                    // build：缓存条目 → 渲染器弹幕对象. 仍然按原来的「每个渲染器各转一次」，
                    // 不合并成一份共享列表（渲染层可能改动自己那份，合并就是行为变更）.
                    var buildStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var masterList = _danmakuMaster is null
                        ? null
                        : BilibiliDanmakuParser.GetDanmakuList(ViewModel.GetCachedDanmakus(), true);
                    var rendererList = _danmakuRenderer is null
                        ? null
                        : BilibiliDanmakuParser.GetDanmakuList(ViewModel.GetCachedDanmakus(), true);
                    timings.BuildMilliseconds = DanmakuRebuildTimings.Elapsed(buildStartedAt);
                    timings.Events = masterList?.Count ?? rendererList?.Count ?? 0;

                    // write：把列表交给渲染层（Win2D 侧会在这一步建池）.
                    var writeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (_danmakuMaster is { } master && masterList is not null)
                    {
                        master.SetDanmakuList(masterList);
                    }

                    if (_danmakuRenderer is { } renderer && rendererList is not null)
                    {
                        renderer.SetDanmakuList(rendererList);
                    }

                    timings.WriteMilliseconds = DanmakuRebuildTimings.Elapsed(writeStartedAt);
                }

                // select：把渲染器时间对准当前进度.
                var selectStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                UpdateDanmakuTime(_lastProgress);
                timings.SelectMilliseconds = DanmakuRebuildTimings.Elapsed(selectStartedAt);

                // style：套样式.
                var styleStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                ResetDanmakuStyle();
                timings.StyleMilliseconds = DanmakuRebuildTimings.Elapsed(styleStartedAt);

                if (!ViewModel.IsPaused)
                {
                    var resumeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    _danmakuRenderer?.Resume();
                    _danmakuMaster?.Resume();
                    timings.ResumeMilliseconds = DanmakuRebuildTimings.Elapsed(resumeStartedAt);
                }

                succeeded = true;
            }
            finally
            {
                CompleteRebuildTiming(sessionId, reason, generation, requestedAt, startedAt, succeeded, timings);
            }
        });
    }

    /// <summary>
    /// 收尾一次重建计时并写日志. 日志本身包了 try/catch：诊断不许把播放带崩.
    /// </summary>
    private void CompleteRebuildTiming(
        string? sessionId,
        string? reason,
        long generation,
        long requestedAt,
        long startedAt,
        bool succeeded,
        DanmakuRebuildTimings timings)
    {
        try
        {
            timings.Failed = !succeeded;
            timings.Stale = !_rebuildGeneration.IsCurrent(generation);
            timings.QueueMilliseconds = (startedAt - requestedAt) * 1000d / System.Diagnostics.Stopwatch.Frequency;
            timings.TotalMilliseconds = DanmakuRebuildTimings.Elapsed(requestedAt);

            // 控成本：日志级别关着就一点格式化都不做.
            if (!Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
            {
                return;
            }

            Serilog.Log.Information(
                "DANMAKU | rebuild session={Session} reason={Reason} events={Events} queue-ms={QueueMs} build-ms={BuildMs} write-ms={WriteMs} style-ms={StyleMs} select-ms={SelectMs} resume-ms={ResumeMs} work-ms={WorkMs} total-ms={TotalMs} stale={Stale} failed={Failed} tick={Tick} utc={Utc}",
                sessionId ?? "-",
                reason ?? "unknown",
                timings.Events,
                timings.QueueMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                timings.BuildMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                timings.WriteMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                timings.StyleMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                timings.SelectMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                timings.ResumeMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                timings.SegmentSumMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                timings.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                timings.Stale,
                timings.Failed,
                startedAt,
                DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            // 诊断失败（日志后端在关闭等）不许影响播放.
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
    }

    private void UpdateDanmakuTime(double pos)
    {
        if (pos < 0 || double.IsNaN(pos))
        {
            return;
        }

        _danmakuRenderer?.UpdateTime(Convert.ToUInt32(pos * 1000));
        _danmakuMaster?.UpdateTime(Convert.ToUInt32(pos * 1000));
    }
}

