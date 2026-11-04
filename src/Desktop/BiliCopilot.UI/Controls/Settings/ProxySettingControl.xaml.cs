// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliCopilot.UI.Controls.Settings;

/// <summary>
/// 播放代理与 CDN 加速设置控件.
/// </summary>
public sealed partial class ProxySettingControl : SettingsPageControlBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProxySettingControl"/> class.
    /// </summary>
    public ProxySettingControl()
    {
        InitializeComponent();
        // 卡片内容可能在 ViewModel 赋值之前就 Load 过，这里等控件自己 Loaded 之后再刷一次.
        Loaded += OnControlLoaded;
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (CdnServerComboBox is not ComboBox combo)
        {
            return;
        }

        combo.DropDownOpened -= OnCdnDropDownOpened;
        combo.DropDownOpened += OnCdnDropDownOpened;
        RefreshServerList(combo);
        Serilog.Log.Information("CDN 下拉控件已初始化（{Count} 项）", combo.Items.Count);
    }

    private void OnCdnDropDownOpened(object? sender, object e)
    {
        if (sender is ComboBox combo)
        {
            RefreshServerList(combo);
        }
    }

    /// <summary>
    /// 加载和每次展开下拉时都重建节点列表，避免出现空列表.
    /// </summary>
    private void OnCdnServerComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        RefreshServerList(combo);
    }

    private void RefreshServerList(ComboBox combo)
    {
        if (ViewModel is not SettingsPageViewModel viewModel)
        {
            return;
        }

        viewModel.RebuildCdnServerList();
        combo.ItemsSource = viewModel.CdnServerCollection;
        var index = viewModel.CdnServerIndex;
        combo.SelectedIndex = index >= 0 && index < combo.Items.Count ? index : (combo.Items.Count > 0 ? 0 : -1);
        Serilog.Log.Information(
            "CDN 下拉已刷新：{Count} 项，选中索引 {Index}，显示 {Text}",
            combo.Items.Count,
            combo.SelectedIndex,
            combo.SelectedItem?.ToString() ?? "(空)");
    }
}