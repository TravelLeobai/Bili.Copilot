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
    public ProxySettingControl() => InitializeComponent();

    /// <summary>
    /// 下拉展开前再刷一遍节点池，确保选项不是空的.
    /// </summary>
    private void OnCdnServerComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo || ViewModel is not SettingsPageViewModel viewModel)
        {
            return;
        }

        viewModel.RebuildCdnServerList();
        combo.ItemsSource = viewModel.CdnServerCollection;
        combo.SelectedIndex = viewModel.CdnServerIndex;
    }
}