// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Specialized;
using System.ComponentModel;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 直播互动列表。
/// 单独拆成一个组件：弹幕每秒几十条，如果跟着父面板整页重渲染，
/// 底部的输入框会被反复重建，输入的字就被吞掉了。
/// </summary>
public sealed class LiveInteractionList : Microsoft.UI.Reactor.Core.Component
{
    private readonly PlayerViewModel _viewModel;
    private ScrollView? _scroll;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveInteractionList"/> class.
    /// </summary>
    /// <param name="viewModel">播放器视图模型.</param>
    public LiveInteractionList(PlayerViewModel viewModel)
        => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var vm = _viewModel;
        var (version, setVersion) = UseState(0);
        _ = version;

        UseEffect(
            () =>
            {
                void OnCollection(object? sender, NotifyCollectionChangedEventArgs e)
                {
                    setVersion(version + 1);
                    ScrollToLatest();
                }

                void OnProperty(object? sender, PropertyChangedEventArgs e)
                {
                    if (e.PropertyName is nameof(PlayerViewModel.LivePanelTab))
                    {
                        setVersion(version + 1);
                    }
                }

                vm.LiveInteractions.CollectionChanged += OnCollection;
                vm.PropertyChanged += OnProperty;
                return () =>
                {
                    vm.LiveInteractions.CollectionChanged -= OnCollection;
                    vm.PropertyChanged -= OnProperty;
                };
            },
            Array.Empty<object>());

        var rows = new List<Element>();
        foreach (var item in vm.LiveInteractions)
        {
            rows.Add(LiveSidePanel.BuildInteractionRow(item));
        }

        if (rows.Count == 0)
        {
            rows.Add(TextBlock("等一条弹幕…")
                .FontSize(12)
                .Opacity(0.6)
                .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Center)
                .Margin(0, 24, 0, 0));
        }

        return ScrollView(VStack(0, [.. rows]).Padding(12, 0, 12, 8))
            .Padding(0)
            .VerticalScrollMode(ScrollingScrollMode.Enabled)
            .OnMountAdd(element =>
            {
                if (element is ScrollView scroll)
                {
                    _scroll = scroll;
                    ScrollToLatest();
                }
            })
            .OnUnmountAdd(element =>
            {
                if (ReferenceEquals(_scroll, element))
                {
                    _scroll = null;
                }
            });
    }

    private void ScrollToLatest()
    {
        var scroll = _scroll;
        if (scroll is null)
        {
            return;
        }

        _ = scroll.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => scroll.ScrollTo(0, scroll.ScrollableHeight));
    }
}
