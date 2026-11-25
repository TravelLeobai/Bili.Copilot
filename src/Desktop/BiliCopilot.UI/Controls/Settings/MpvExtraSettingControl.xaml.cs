// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Controls.Settings;

public sealed partial class MpvExtraSettingControl : SettingsPageControlBase
{
    private bool _isLoaded;

    public MpvExtraSettingControl()
    {
        InitializeComponent();
    }

    protected override void OnControlLoaded()
    {
        AudioChannelLayoutComboBox.SelectedIndex = (int)ViewModel.PreferAudioChannelLayout;
        ProfileComboBox.SelectedIndex = (int)ViewModel.MpvBuiltInProfile;
        HrSeekComboBox.SelectedIndex = (int)ViewModel.HrSeek;
        MaxVolumeBox.Value = ViewModel.MaxVolume;
        SyncCacheNumbers();

        _isLoaded = true;
    }

    /// <summary>
    /// 把缓冲数字框刷成 ViewModel 里的值.
    /// NumberBox 用 ValueChanged 写回设置（不是双向绑定），所以「恢复默认」之后必须回来同步一次，
    /// 否则界面上还留着改之前的老数字.
    /// </summary>
    private void SyncCacheNumbers()
    {
        CacheSizeBox.Value = ViewModel.MaxCacheSize;
        CacheSecondsBox.Value = ViewModel.MaxCacheSeconds;
        BackCacheSizeBox.Value = ViewModel.MaxBackCacheSize;
        CacheSecsLimitBox.Value = ViewModel.CacheSecsLimit;
    }

    private void OnAudioChannelLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        var selectedIndex = AudioChannelLayoutComboBox.SelectedIndex;
        ViewModel.PreferAudioChannelLayout = (AudioChannelLayoutType)selectedIndex;
    }

    private void OnCacheSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isLoaded)
        {
            return;
        }

        ViewModel.MaxCacheSize = (int)args.NewValue;
    }

    private void OnCacheSecondsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isLoaded)
        {
            return;
        }

        ViewModel.MaxCacheSeconds = (int)args.NewValue;
    }

    private void OnCacheSecsLimitChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isLoaded)
        {
            return;
        }

        ViewModel.CacheSecsLimit = (int)args.NewValue;
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        var selectedIndex = ProfileComboBox.SelectedIndex;
        ViewModel.MpvBuiltInProfile = (MpvBuiltInProfile)selectedIndex;
    }

    private void OnHrSeekChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        var selectedIndex = HrSeekComboBox.SelectedIndex;
        ViewModel.HrSeek = (HrSeekType)selectedIndex;
    }

    private void OnMaxVolumeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isLoaded)
        {
            return;
        }

        ViewModel.MaxVolume = (int)args.NewValue;
    }

    private async void OnCacheDirButtonClick(object sender, RoutedEventArgs e)
    {
        var folder = await this.Get<IFileToolkit>().PickFolderAsync(this.Get<AppViewModel>().ActivatedWindow);
        if (folder != null)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.CacheDir, folder.Path);
            ViewModel.CacheDirDescription = folder.Path;
            ViewModel.HasCustomCacheDir = true;
        }
    }

    private void OnRemoveCacheDirButtonClick(object sender, RoutedEventArgs e)
    {
        SettingsToolkit.DeleteLocalSetting(SettingNames.CacheDir);
        ViewModel.CacheDirDescription = "--demuxer-cache-dir";
        ViewModel.HasCustomCacheDir = false;
    }

    private void OnBackCacheSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isLoaded)
        {
            return;
        }

        ViewModel.MaxBackCacheSize = (int)args.NewValue;
    }

    /// <summary>
    /// 只重置「缓冲与缓存」这一组，不动其它播放器设置，也不动用户的 mpv.conf.
    /// </summary>
    private void OnResetCacheOptionsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetMpvCacheOptionsCommand.Execute(default);

        // 界面上的数字框不是双向绑定，重置完要同步回来.
        SyncCacheNumbers();
    }
}
