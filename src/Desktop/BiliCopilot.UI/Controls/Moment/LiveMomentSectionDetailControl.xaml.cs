// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Moment;
using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Components;
using Microsoft.UI.Reactor.Hosting;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 直播分区（动态页新增的分段）：过滤栏与卡片瀑布流都由 Reactor 渲染，这里只做宿主.
/// </summary>
public sealed partial class LiveMomentSectionDetailControl : LiveSectionControlBase
{
    private LiveUperBar _uperBar = new();
    private ReactorHostControl? _cardHost;
    private bool _cardMounted;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentSectionDetailControl"/> class.
    /// </summary>
    public LiveMomentSectionDetailControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (UperHost.Children.Count == 0)
        {
            UperHost.Children.Add(_uperBar);
        }

        if (_cardHost is null)
        {
            _cardHost = new ReactorHostControl();
            // 挂载要等宿主真的进到可视树里：太早 Mount 组件拿不到尺寸，整片区域会空白.
            _cardHost.Loaded += (_, _) =>
            {
                if (!_cardMounted && _cardHost is not null && ViewModel is not null)
                {
                    _cardMounted = true;
                    _cardHost.Mount(new LiveMomentWaterfall(ViewModel));
                }
            };
            CardHost.Children.Add(_cardHost);
        }

        _uperBar.Bind(ViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _cardHost?.Dispose();
        _cardHost = null;
        _cardMounted = false;
        UperHost.Children.Clear();
        CardHost.Children.Clear();
    }
}

/// <summary>
/// 直播分区控件基类.
/// </summary>
public abstract class LiveSectionControlBase : LayoutUserControlBase<LiveMomentSectionDetailViewModel>
{
}



