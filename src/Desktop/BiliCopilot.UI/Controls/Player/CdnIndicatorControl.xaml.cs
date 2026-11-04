// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BiliCopilot.UI.Toolkits;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 播放界面右上角的 CDN 指示按钮：显示正在使用的节点，点开可以开关优选、换节点.
/// </summary>
public sealed partial class CdnIndicatorControl : UserControl
{
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CdnIndicatorControl"/> class.
    /// </summary>
    public CdnIndicatorControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            CdnAccelerator.HostChanged += OnHostChanged;
            _subscribed = true;
        }

        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            CdnAccelerator.HostChanged -= OnHostChanged;
            _subscribed = false;
        }
    }

    private void OnHostChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            Refresh();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    private void Refresh()
    {
        var enabled = CdnAccelerator.IsEnabled;
        var host = CdnAccelerator.ActiveHostDisplay;

        ToggleItem.IsChecked = enabled;
        SwitchItem.IsEnabled = enabled && CdnAccelerator.Mode != CdnReplaceMode.None;
        StateIcon.Symbol = FluentIcons.Common.Symbol.Globe;
        StateIcon.Opacity = enabled ? 1 : 0.4;

        HostText.Text = enabled
            ? (string.IsNullOrWhiteSpace(host) ? "CDN" : host)
            : "CDN 关";

        InfoItem.Text = enabled
            ? $"节点池 {CdnPoolService.Count} 个 · {(CdnAccelerator.AutoSelect ? "自动优选" : "手动节点")}"
            : "CDN 替换已关闭";
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        CdnAccelerator.SetEnabled(!CdnAccelerator.IsEnabled);
        Refresh();
    }

    private async void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        if (CdnAccelerator.GetRankedHosts().Count == 0)
        {
            // 还没测过速，先拿节点池再轮换，避免只在内置节点里打转.
            await CdnPoolService.SyncAsync();
        }

        CdnAccelerator.RotateHost();
        Refresh();
    }
}