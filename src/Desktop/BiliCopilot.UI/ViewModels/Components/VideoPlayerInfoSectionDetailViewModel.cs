// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 视频播放页信息区块详情视图模型.
/// </summary>
public sealed partial class VideoPlayerInfoSectionDetailViewModel : ViewModelBase, IPlayerSectionDetailViewModel
{
    // 信息面板的数据由 VideoConnectorViewModel 在 InitializeAsync 里一次性拉取并填充，
    // 面板自身没有独立的网络请求，所以首次加载的三段测量都挂在这里，由连接器在真实数据路径上调用。
    private static readonly Stopwatch FirstLoadStopwatch = new();
    private static int _firstLoadActive;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPlayerInfoSectionDetailViewModel"/> class.
    /// </summary>
    public VideoPlayerInfoSectionDetailViewModel(VideoConnectorViewModel pageVM)
        => Page = pageVM;

    /// <summary>
    /// 视频播放页视图模型.
    /// </summary>
    public VideoConnectorViewModel Page { get; }

    /// <inheritdoc/>
    public string Title { get; } = ResourceToolkit.GetLocalizedString(StringNames.Information);

    /// <summary>
    /// 开始一次"详情面板首次加载"的计时（每个视频一次，同一视频的刷新不会重复计时）.
    /// 只在性能探针启用时生效，探针关闭时什么都不做。
    /// </summary>
    internal static void BeginFirstLoadProbe()
    {
        if (!PerfProbe.IsEnabled)
        {
            return;
        }

        _firstLoadActive = 1;
        FirstLoadStopwatch.Restart();
        PerfProbe.Count("Detail.FirstLoad.Start");
    }

    /// <summary>
    /// 开始计"拉取详情数据"那一次 await 的网络耗时.
    /// 返回 null 表示探针关闭或不在首次加载路径上，此时调用方不用管返回值。
    /// </summary>
    /// <returns>计时器，或 null.</returns>
    internal static Stopwatch? BeginNetworkProbe()
        => !PerfProbe.IsEnabled || _firstLoadActive == 0 ? null : Stopwatch.StartNew();

    /// <summary>
    /// 结束网络耗时计时并记录.
    /// </summary>
    /// <param name="watch"><see cref="BeginNetworkProbe"/> 的返回值.</param>
    internal static void EndNetworkProbe(Stopwatch? watch)
    {
        if (watch is null)
        {
            return;
        }

        watch.Stop();
        PerfProbe.Record("Detail.FirstLoad.Network", watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 详情面板填充完成：记录首次加载总耗时和条目数.
    /// </summary>
    /// <param name="itemCount">面板里按条渲染的条目数（视频标签数量）.</param>
    internal static void CompleteFirstLoadProbe(int itemCount)
    {
        if (!PerfProbe.IsEnabled || _firstLoadActive == 0)
        {
            return;
        }

        _firstLoadActive = 0;
        FirstLoadStopwatch.Stop();
        PerfProbe.Record("Detail.FirstLoad.Total", FirstLoadStopwatch.Elapsed.TotalMilliseconds);
        PerfProbe.Record("Detail.FirstLoad.ItemCount", itemCount);
    }

    /// <summary>
    /// 首次加载失败（拉取详情抛异常）：丢掉这次计时，不产生半截样本.
    /// </summary>
    internal static void AbortFirstLoadProbe()
    {
        if (_firstLoadActive == 0)
        {
            return;
        }

        _firstLoadActive = 0;
        FirstLoadStopwatch.Stop();
    }

    [RelayCommand]
    private static Task TryFirstLoadAsync()
        => Task.CompletedTask;
}
