// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 播放界面右上角的空降助手 / AI 跳过按钮：显示开关状态，点开可以直接切换.
/// </summary>
public sealed partial class AdSkipIndicatorControl : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AdSkipIndicatorControl"/> class.
    /// </summary>
    public AdSkipIndicatorControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        var sponsorEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.EnableSponsorBlock, true);
        var manual = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockManualMode, false);
        var aiEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdEnabled, false);
        var audioEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.AudioAdEnabled, false);
        var endpoint = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdEndpoint, string.Empty);
        var key = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdKey, string.Empty);
        var configured = !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key);

        SponsorToggleItem.IsChecked = sponsorEnabled;
        SponsorManualItem.IsChecked = manual;
        SponsorManualItem.IsEnabled = sponsorEnabled;
        SponsorIcon.Symbol = FluentIcons.Common.Symbol.FastForward;
        SponsorIcon.Opacity = sponsorEnabled ? 1 : 0.4;
        SponsorText.Text = !sponsorEnabled ? "空降关" : (manual ? "空降·提示" : "空降");
        SponsorInfoItem.Text = sponsorEnabled
            ? (manual ? "识别到广告会提示，不自动跳" : "识别到广告自动跳过")
            : "空降助手已关闭";

        AiToggleItem.IsChecked = aiEnabled;
        AiAudioItem.IsChecked = audioEnabled;
        AiAudioItem.IsEnabled = aiEnabled;
        AiIcon.Symbol = FluentIcons.Common.Symbol.Sparkle;
        AiIcon.Opacity = aiEnabled ? 1 : 0.4;
        AiText.Text = aiEnabled ? "AI 跳过" : "AI 关";
        AiInfoItem.Text = !configured
            ? "还没配 AI 接口，去设置里填地址和密钥"
            : aiEnabled
                ? $"模型 {SettingsToolkit.ReadLocalSetting(SettingNames.AiAdModel, "未填")} · {(audioEnabled ? "含音频" : "仅字幕")}"
                : "AI 识别已关闭";
    }

    private void OnSponsorToggleClick(object sender, RoutedEventArgs e)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.EnableSponsorBlock, SponsorToggleItem.IsChecked);
        Refresh();
    }

    private void OnSponsorManualClick(object sender, RoutedEventArgs e)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.SponsorBlockManualMode, SponsorManualItem.IsChecked);
        Refresh();
    }

    private void OnAiToggleClick(object sender, RoutedEventArgs e)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.AiAdEnabled, AiToggleItem.IsChecked);
        Refresh();
    }

    private void OnAiAudioClick(object sender, RoutedEventArgs e)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.AudioAdEnabled, AiAudioItem.IsChecked);
        Refresh();
    }
}