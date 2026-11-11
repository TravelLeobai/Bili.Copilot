// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.AI;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.Pages;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Humanizer;
using Microsoft.Extensions.Logging;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core.Enums;
using Richasy.WinUIKernel.AI.ViewModels;
using Richasy.WinUIKernel.Share;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using Windows.Storage;
using Windows.System;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 设置页面视图模型.
/// </summary>
public sealed partial class SettingsPageViewModel : AISettingsViewModelBase
{
    /// <summary>
    /// 「自动优选」这一项不对应具体主机，选中它等于开启自动优选.
    /// </summary>
    public const string AutoCdnServerOption = "自动优选（跟随测速）";

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            await LoadCacheSizeAsync();
            return;
        }

        ScrollAccelerate = SettingsToolkit.ReadLocalSetting(SettingNames.ScrollAccelerate, true);
        AppTheme = SettingsToolkit.ReadLocalSetting(SettingNames.AppTheme, ElementTheme.Default);
        CheckTheme();
        LogLevel = SettingsToolkit.ReadLocalSetting(SettingNames.MpvLogLevel, MpvLogLevel.Warn);
        HideMainWindowOnPlay = SettingsToolkit.ReadLocalSetting(SettingNames.HideMainWindowOnPlay, true);
        IsAcrylicBackdropEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.EnableAcrylicBackdrop, true);
        IsSponsorBlockEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.EnableSponsorBlock, true);
        MomentVideoLayoutPreset = Math.Clamp(SettingsToolkit.ReadLocalSetting(SettingNames.MomentVideoLayoutPreset, 0), 0, 4);
        MomentVideoOpenedMarkStyleCollection = Enum.GetValues<MomentVideoOpenedMarkStyle>().ToList();
        MomentVideoOpenedMarkStyle = SettingsToolkit.ReadLocalSetting(
            SettingNames.MomentVideoOpenedMarkStyle,
            Models.Constants.MomentVideoOpenedMarkStyle.AccentBorder);
        IsAutoPlayNextRecommendVideo = SettingsToolkit.ReadLocalSetting(SettingNames.AutoPlayNextRecommendVideo, false);
        PlayNextWithoutTip = SettingsToolkit.ReadLocalSetting(SettingNames.PlayNextWithoutTip, false);
        EndWithPlaylist = SettingsToolkit.ReadLocalSetting(SettingNames.EndWithPlaylist, true);
        IsCopyScreenshot = SettingsToolkit.ReadLocalSetting(SettingNames.CopyAfterScreenshot, true);
        IsOpenScreenshotFile = SettingsToolkit.ReadLocalSetting(SettingNames.OpenAfterScreenshot, true);
        PlayerSpeedEnhancement = SettingsToolkit.ReadLocalSetting(SettingNames.IsPlayerSpeedEnhancement, false);
        GlobalPlayerSpeed = SettingsToolkit.ReadLocalSetting(SettingNames.IsPlayerSpeedShare, true);
        HideWhenCloseWindow = SettingsToolkit.ReadLocalSetting(SettingNames.HideWhenCloseWindow, false);
        IsNotificationEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.IsNotificationEnabled, true);
        IsVideoMomentNotificationEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.IsVideoMomentNotificationEnabled, true);
        IsLiveStartNotificationEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.IsLiveStartNotificationEnabled, true);
        IsReplyNotificationEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.IsReplyNotificationEnabled, true);
        IsSeasonUpdateNotificationEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.IsSeasonUpdateNotificationEnabled, true);
        NoP2P = SettingsToolkit.ReadLocalSetting(SettingNames.PlayWithoutP2P, false);
        CdnServer = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerCdnServer, Toolkits.PlayUrlOptimizer.DefaultCdnHost);
        PreferWebEngine = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerEngine, PlayerEngineType.Mpv) == PlayerEngineType.Web;
        RememberPlaybackEnhancements = SettingsToolkit.ReadLocalSetting(SettingNames.RememberPlaybackEnhancements, true);
        CdnPoolStatusText = CdnPoolService.StatusText;
        SponsorManualMode = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockManualMode, false);
        SponsorMinDuration = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockMinDuration, 3d);
        SponsorIncludeSelfPromo = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeSelfPromo, true);
        SponsorIncludeIntro = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeIntro, false);
        SponsorIncludeOutro = SettingsToolkit.ReadLocalSetting(SettingNames.SponsorBlockIncludeOutro, false);
        AdCloudEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.AdCloudEnabled, false);
        AdCloudUrl = SettingsToolkit.ReadLocalSetting(SettingNames.AdCloudUrl, string.Empty);
        AiAdEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdEnabled, false);
        AiInterestEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.AiInterestEnabled, true);
        AiInterestMaxLength = SettingsToolkit.ReadLocalSetting(SettingNames.AiInterestMaxLength, 60);
        AiAdEndpoint = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdEndpoint, string.Empty);
        AiAdKey = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdKey, string.Empty);
        AiAdModel = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdModel, string.Empty);
        AiAdPrompt = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdPrompt, Services.AiAdAnalyzer.DefaultPrompt);
        AiAutoSave = SettingsToolkit.ReadLocalSetting(SettingNames.AiAutoSave, true);
        AiTestStatusText = string.Empty;
        AudioAdEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.AudioAdEnabled, false);
        DashScopeKey = SettingsToolkit.ReadLocalSetting(SettingNames.DashScopeKey, string.Empty);
        DashScopeModel = SettingsToolkit.ReadLocalSetting(SettingNames.DashScopeModel, "paraformer-v2");
        QualityUnlockEnabled = Services.QualityUnlockService.IsEnabled;
        QualityUnlockAutoMax = Services.QualityUnlockService.AutoMaxQuality;
        QualityUnlockServer = Services.QualityUnlockService.Server;
        QualityUnlockHomePage = Services.QualityUnlockService.HomePage;
        QualityUnlockStatusText = Services.QualityUnlockService.LastSync == DateTimeOffset.UnixEpoch
            ? "尚未同步"
            : $"上次同步：{Services.QualityUnlockService.LastSync:MM-dd HH:mm}";
        AutoFallbackToWeb = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerAutoFallback, true);
        ProxyEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.ProxyEnabled, false);
        ProxyServer = SettingsToolkit.ReadLocalSetting(SettingNames.ProxyServer, "https://bili.nepnep.moe");
        ProxyServerHk = SettingsToolkit.ReadLocalSetting(SettingNames.ProxyServerHk, string.Empty);
        ProxyServerTw = SettingsToolkit.ReadLocalSetting(SettingNames.ProxyServerTw, string.Empty);
        ProxyServerCn = SettingsToolkit.ReadLocalSetting(SettingNames.ProxyServerCn, string.Empty);
        ReplaceCdnEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.ReplaceCdnEnabled, true);
        CdnReplaceMode = SettingsToolkit.ReadLocalSetting(SettingNames.CdnReplaceMode, CdnReplaceMode.PcdnOnly);
        CdnReplaceModeIndex = (int)CdnReplaceMode;
        CdnAutoSelect = SettingsToolkit.ReadLocalSetting(SettingNames.CdnAutoSelect, true);
        CdnFilterPort = SettingsToolkit.ReadLocalSetting(SettingNames.CdnFilterPort, true);
        CdnRewriteAkamai = SettingsToolkit.ReadLocalSetting(SettingNames.CdnRewriteAkamai, false);
        CdnStallSwitch = SettingsToolkit.ReadLocalSetting(SettingNames.CdnStallSwitch, true);
        CdnReplaceModeCollection = Enum.GetValues<CdnReplaceMode>().ToList();
        CdnServerSelected = SettingsToolkit.ReadLocalSetting(SettingNames.PlayerCdnServer, PlayUrlOptimizer.DefaultCdnHost);
        RebuildCdnServerList();
        PlayerDisplayModeCollection = Enum.GetValues<PlayerDisplayMode>().ToList();
        PreferCodecCollection = Enum.GetValues<PreferCodecType>().ToList();
        PreferQualityCollection = Enum.GetValues<PreferQualityType>().ToList();
        PreferDecodeCollection = Enum.GetValues<PreferDecodeType>().ToList();
        MTCBehaviorCollection = Enum.GetValues<MTCBehavior>().ToList();
        DefaultPlayerDisplayMode = SettingsToolkit.ReadLocalSetting(SettingNames.DefaultPlayerDisplayMode, PlayerDisplayMode.Default);
        PreferCodec = SettingsToolkit.ReadLocalSetting(SettingNames.PreferCodec, PreferCodecType.H264);
        PreferQuality = SettingsToolkit.ReadLocalSetting(SettingNames.PreferQuality, PreferQualityType.Auto);
        IsPopularNavVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(PopularPage).Name}Visible", true);
        IsMomentNavVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(MomentPage).Name}Visible", true);
        IsVideoNavVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(VideoPartitionPage).Name}Visible", true);
        IsLiveNavVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(LivePartitionPage).Name}Visible", true);
        IsSearchHomeNavVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(SearchHomePage).Name}Visible", true);
        IsAnimeNavVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(AnimePage).Name}Visible", true);
        IsCinemaNavVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(CinemaPage).Name}Visible", true);
        IsArticleNavVisible = this.Get<ISettingsToolkit>().ReadLocalSetting($"Is{typeof(ArticlePartitionPage).Name}Visible", true);
        IsTopNavShown = SettingsToolkit.ReadLocalSetting(SettingNames.IsTopNavBarShown, true);
        StepBackwardSecond = SettingsToolkit.ReadLocalSetting(SettingNames.StepBackwardSecond, 10d);
        StepForwardSecond = SettingsToolkit.ReadLocalSetting(SettingNames.StepForwardSecond, 30d);
        PreferAudioChannelLayout = SettingsToolkit.ReadLocalSetting(SettingNames.PreferAudioChannelLayout, Richasy.MpvKernel.Core.Enums.AudioChannelLayoutType.Auto);
        MpvBuiltInProfile = SettingsToolkit.ReadLocalSetting(SettingNames.MpvBuiltInProfile, MpvBuiltInProfile.HighQuality);
        UseIntegrationWhenSinglePlayWindow = SettingsToolkit.ReadLocalSetting(SettingNames.UseIntegrationWhenSinglePlayWindow, true);
        MaxCacheSize = SettingsToolkit.ReadLocalSetting(SettingNames.MaxCacheSize, 300d); // Default to 300 MB
        MaxBackCacheSize = SettingsToolkit.ReadLocalSetting(SettingNames.MaxBackCacheSize, 0d); // Default to 0 MB
        MaxCacheSeconds = SettingsToolkit.ReadLocalSetting(SettingNames.MaxCacheSeconds, 300d); // Default to 300 seconds
        HrSeek = SettingsToolkit.ReadLocalSetting(SettingNames.HrSeek, HrSeekType.Default);
        MaxVolume = SettingsToolkit.ReadLocalSetting(SettingNames.MaxVolume, 100d);
        AudioExclusiveEnabled = SettingsToolkit.ReadLocalSetting(SettingNames.AudioExclusiveEnabled, false);
        CacheOnDisk = SettingsToolkit.ReadLocalSetting(SettingNames.CacheOnDisk, false);
        var cacheDir = SettingsToolkit.ReadLocalSetting(SettingNames.CacheDir, string.Empty);
        HasCustomCacheDir = !string.IsNullOrEmpty(cacheDir);
        CacheDirDescription = string.IsNullOrEmpty(cacheDir) ? "--demuxer-cache-dir" : cacheDir;
        TempPlaybackRate = SettingsToolkit.ReadLocalSetting(SettingNames.TempPlaybackRate, 3.0);
        try
        {
            PreferDecode = SettingsToolkit.ReadLocalSetting(SettingNames.PreferDecode, PreferDecodeType.Auto);
        }
        catch (Exception)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.PreferDecode, PreferDecodeType.Auto);
            PreferDecode = PreferDecodeType.Auto;
        }

        MTCBehavior = SettingsToolkit.ReadLocalSetting(SettingNames.MTCBehavior, MTCBehavior.Automatic);
        BottomProgressVisible = SettingsToolkit.ReadLocalSetting(SettingNames.IsBottomProgressBarVisible, true);
        DefaultDownloadPath = SettingsToolkit.ReadLocalSetting(SettingNames.DownloadFolder, string.Empty);
        UseExternalBBDown = SettingsToolkit.ReadLocalSetting(SettingNames.UseExternalBBDown, false);
        OnlyCopyCommandWhenDownload = SettingsToolkit.ReadLocalSetting(SettingNames.OnlyCopyCommandWhenDownload, false);
        WithoutCredentialWhenGenDownloadCommand = SettingsToolkit.ReadLocalSetting(SettingNames.WithoutCredentialWhenGenDownloadCommand, false);
        FilterAISubtitle = SettingsToolkit.ReadLocalSetting(SettingNames.FilterAISubtitle, true);
        IsAIStreamingResponse = SettingsToolkit.ReadLocalSetting(SettingNames.IsAIStreamingResponse, true);
        if (string.IsNullOrEmpty(DefaultDownloadPath))
        {
            DefaultDownloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Bili Downloads");
        }

        OpenFolderAfterDownload = SettingsToolkit.ReadLocalSetting(SettingNames.OpenFolderAfterDownload, true);
        DownloadWithDanmaku = SettingsToolkit.ReadLocalSetting(SettingNames.DownloadWithDanmaku, false);
        UseWebPlayerWhenLive = SettingsToolkit.ReadLocalSetting(SettingNames.UseWebPlayerWhenLive, false);
        ShowSearchRecommend = SettingsToolkit.ReadLocalSetting(SettingNames.ShowSearchRecommend, false);

        ScreenshotAction = SettingsToolkit.ReadLocalSetting(SettingNames.ScreenshotAction, ScreenshotAction.Open);
        var customScreenshotFolderPath = SettingsToolkit.ReadLocalSetting(SettingNames.CustomScreenshotFolderPath, string.Empty);
        if (string.IsNullOrEmpty(customScreenshotFolderPath) || !Directory.Exists(customScreenshotFolderPath))
        {
            ScreenshotFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Bili screenshots");
        }
        else
        {
            ScreenshotFolderPath = customScreenshotFolderPath;
        }

        var copyrightTemplate = ResourceToolkit.GetLocalizedString(StringNames.Copyright);
        Copyright = string.Format(copyrightTemplate, 2024);
        PackageVersion = this.Get<IAppToolkit>().GetPackageVersion();
        CheckMpvConfigVisible();
        LoadCustomLibmpvSettings();
        await LoadCacheSizeAsync();

        if (GpuDevices.Count == 0)
        {
            GpuDevices.Add("Auto");
            foreach (var name in AppToolkit.GetGpuNames())
            {
                GpuDevices.Add(name);
            }

            var localD3D11Name = SettingsToolkit.ReadLocalSetting(SettingNames.D3D11AdapterName, "Auto");
            D3D11AdapterName = GpuDevices.Contains(localD3D11Name) ? localD3D11Name : "Auto";
            var localVulkanName = SettingsToolkit.ReadLocalSetting(SettingNames.VulkanDeviceName, "Auto");
            VulkanDeviceName = GpuDevices.Contains(localVulkanName) ? localVulkanName : "Auto";
        }

        _isInitialized = true;
    }

    [RelayCommand]
    private async Task OpenMpvConfigInEditorAsync()
    {
        try
        {
            var path = await AppToolkit.EnsureMpvConfigExistAsync();
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask();
            var isSuccess = await Launcher.LaunchFileAsync(file).AsTask();
            if (!isSuccess)
            {
                await AppToolkit.OpenAndSelectFileInExplorerAsync(file.Path, false);
            }

            var isFirstOpen = SettingsToolkit.ReadLocalSetting(SettingNames.FirstOpenMpvEditor, true);
            if (isFirstOpen)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceToolkit.GetLocalizedString(StringNames.Tip),
                    Content = ResourceToolkit.GetLocalizedString(StringNames.FirstOpenMpvConfigTip),
                    CloseButtonText = ResourceToolkit.GetLocalizedString(StringNames.Confirm),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.Get<IXamlRootProvider>().XamlRoot,
                };

                await dialog.ShowAsync();
                SettingsToolkit.WriteLocalSetting(SettingNames.FirstOpenMpvEditor, false);
            }
        }
        catch (Exception ex)
        {
            this.Get<ILogger<SettingsPageViewModel>>().LogError(ex, "打开MPV配置文件失败");
            this.Get<AppViewModel>().ShowTipCommand.Execute((ex.Message, InfoType.Error));
        }
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var dialog = new ContentDialog
        {
            Title = ResourceToolkit.GetLocalizedString(StringNames.ClearCache),
            Content = ResourceToolkit.GetLocalizedString(StringNames.ClearCacheTip),
            PrimaryButtonText = ResourceToolkit.GetLocalizedString(StringNames.Confirm),
            CloseButtonText = ResourceToolkit.GetLocalizedString(StringNames.Cancel),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Get<IXamlRootProvider>().XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var cacheFolder = await Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalCacheFolder.CreateFolderAsync("ImageExCache", CreationCollisionOption.OpenIfExists);
            await cacheFolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
            await LoadCacheSizeAsync();
        }
    }

    private async Task LoadCacheSizeAsync()
    {
        var cacheFolder = await Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalCacheFolder.CreateFolderAsync("ImageExCache", CreationCollisionOption.OpenIfExists);
        var size = Directory.GetFiles(cacheFolder.Path, "*.*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        IsTotalImageCacheClearEnabled = size > 0;
        if (size == 0)
        {
            TotalImageCacheSize = ResourceToolkit.GetLocalizedString(StringNames.NoCache);
        }
        else
        {
            TotalImageCacheSize = size.Bits().Humanize();
        }
    }

    private void LoadCustomLibmpvSettings()
    {
        var customLibmpvPath = SettingsToolkit.ReadLocalSetting(SettingNames.CustomLibmpvPath, string.Empty);
        var isValidMpvPath = !string.IsNullOrEmpty(customLibmpvPath) && File.Exists(customLibmpvPath);
        if (!isValidMpvPath && !string.IsNullOrEmpty(customLibmpvPath))
        {
            SettingsToolkit.DeleteLocalSetting(SettingNames.CustomLibmpvPath);
        }

        _initialCustomLibMpvPath = isValidMpvPath ? customLibmpvPath : ResourceToolkit.GetLocalizedString(StringNames.InternalLibMpv);
        CustomLibMpvPath = _initialCustomLibMpvPath;
        CanResetLibMpvPath = isValidMpvPath;
    }

    private void CheckRestartTip()
        => IsRestartTipShown = CustomLibMpvPath != _initialCustomLibMpvPath;

    [RelayCommand]
    private async Task SelectLibMpvFileAsync()
    {
        var file = await this.Get<IFileToolkit>().PickFileAsync(".dll", this.Get<AppViewModel>().ActivatedWindow);
        if (file is not null)
        {
            CustomLibMpvPath = file.Path;
            SettingsToolkit.WriteLocalSetting(SettingNames.CustomLibmpvPath, CustomLibMpvPath);
            CanResetLibMpvPath = true;
        }

        CheckRestartTip();
    }

    [RelayCommand]
    private void ResetLibMpvPath()
    {
        CustomLibMpvPath = ResourceToolkit.GetLocalizedString(StringNames.InternalLibMpv);
        SettingsToolkit.DeleteLocalSetting(SettingNames.CustomLibmpvPath);
        CanResetLibMpvPath = false;
        CheckRestartTip();
    }

    [RelayCommand]
    private async Task ChooseDownloadFolderAsync()
    {
        var folder = await this.Get<IFileToolkit>().PickFolderAsync(this.Get<AppViewModel>().ActivatedWindow);
        if (folder != null)
        {
            DefaultDownloadPath = folder.Path;
        }
    }

    [RelayCommand]
    private async Task OpenDownloadFolderAsync()
    {
        if (!Directory.Exists(DefaultDownloadPath))
        {
            Directory.CreateDirectory(DefaultDownloadPath);
        }

        await Launcher.LaunchFolderPathAsync(DefaultDownloadPath);
    }

    [RelayCommand]
    private async Task EditVideoSummarizeAsync()
    {
        if (!FileToolkit.IsLocalDataExist("video_summarize.txt", "Prompt"))
        {
            await this.Get<IFileToolkit>().WriteLocalDataAsync("video_summarize.txt", PromptConstants.VideoSummaryPrompt, default, "Prompt");
        }

        await new CustomPromptDialog("video_summarize", ResourceToolkit.GetLocalizedString(StringNames.VideoSummarize))
        { XamlRoot = this.Get<AppViewModel>().ActivatedWindow.Content.XamlRoot }.ShowAsync();
    }

    [RelayCommand]
    private async Task EditVideoEvaluationAsync()
    {
        if (!FileToolkit.IsLocalDataExist("video_evaluation.txt", "Prompt"))
        {
            await this.Get<IFileToolkit>().WriteLocalDataAsync("video_evaluation.txt", PromptConstants.VideoEvaluationPrompt, default, "Prompt");
        }

        await new CustomPromptDialog("video_evaluation", ResourceToolkit.GetLocalizedString(StringNames.VideoEvaluation))
        { XamlRoot = this.Get<AppViewModel>().ActivatedWindow.Content.XamlRoot }.ShowAsync();
    }

    [RelayCommand]
    private async Task EditArticleSummarizeAsync()
    {
        if (!FileToolkit.IsLocalDataExist("article_summarize.txt", "Prompt"))
        {
            await this.Get<IFileToolkit>().WriteLocalDataAsync("article_summarize.txt", PromptConstants.ArticleSummaryPrompt, default, "Prompt");
        }

        await new CustomPromptDialog("article_summarize", ResourceToolkit.GetLocalizedString(StringNames.ArticleSummarize))
        { XamlRoot = this.Get<AppViewModel>().ActivatedWindow.Content.XamlRoot }.ShowAsync();
    }

    [RelayCommand]
    private async Task EditArticleEvaluationAsync()
    {
        if (!FileToolkit.IsLocalDataExist("article_evaluation.txt", "Prompt"))
        {
            await this.Get<IFileToolkit>().WriteLocalDataAsync("article_evaluation.txt", PromptConstants.ArticleEvaluationPrompt, default, "Prompt");
        }

        await new CustomPromptDialog("article_evaluation", ResourceToolkit.GetLocalizedString(StringNames.ArticleEvaluation))
        { XamlRoot = this.Get<AppViewModel>().ActivatedWindow.Content.XamlRoot }.ShowAsync();
    }

    [RelayCommand]
    private async Task PickScreenshotFolderAsync()
    {
        var folder = await this.Get<IFileToolkit>().PickFolderAsync(this.Get<AppViewModel>().ActivatedWindow);
        if (folder != null)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.CustomScreenshotFolderPath, folder.Path);
            ScreenshotFolderPath = folder.Path;
        }
    }

    private void CheckMpvConfigVisible()
    {
        IsMpvCustomOptionVisible = PreferDecode == PreferDecodeType.Custom;
        if (IsMpvCustomOptionVisible)
        {
            IsMpvExtraSettingExpanded = false;
        }
    }

    private void CheckTheme()
    {
        AppThemeText = AppTheme switch
        {
            ElementTheme.Light => ResourceToolkit.GetLocalizedString(StringNames.LightTheme),
            ElementTheme.Dark => ResourceToolkit.GetLocalizedString(StringNames.DarkTheme),
            _ => ResourceToolkit.GetLocalizedString(StringNames.SystemDefault),
        };
    }

    private void WriteNavVisibleSetting(Type pageType, bool isVisible)
    {
        this.Get<ISettingsToolkit>().WriteLocalSetting($"Is{pageType.Name}Visible", isVisible);
        this.Get<NavigationViewModel>().SetNavItemVisibility(pageType, isVisible);
    }

    partial void OnAppThemeChanged(ElementTheme value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.AppTheme, value);
        CheckTheme();
        this.Get<AppViewModel>().ChangeThemeCommand.Execute(value);
    }

    partial void OnLogLevelChanged(MpvLogLevel value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.MpvLogLevel, value);

    partial void OnIsTopNavShownChanged(bool value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.IsTopNavBarShown, value);
        // 立刻同步给导航壳，不用重启.
        this.Get<NavigationViewModel>().IsTopNavBarShown = value;
    }

    partial void OnHideMainWindowOnPlayChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.HideMainWindowOnPlay, value);

    partial void OnBlockLowLevelUsersChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.BlockLowLevelUsers, value);

    partial void OnStepBackwardSecondChanged(double value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.StepBackwardSecond, value);

    partial void OnStepForwardSecondChanged(double value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.StepForwardSecond, value);

    partial void OnMaxCacheSizeChanged(double value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.MaxCacheSize, value);

    partial void OnMaxCacheSecondsChanged(double value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.MaxCacheSeconds, value);

    partial void OnMpvBuiltInProfileChanged(MpvBuiltInProfile value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.MpvBuiltInProfile, value);

    partial void OnHrSeekChanged(HrSeekType value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.HrSeek, value);

    partial void OnUseIntegrationWhenSinglePlayWindowChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.UseIntegrationWhenSinglePlayWindow, value);

    partial void OnPreferAudioChannelLayoutChanged(AudioChannelLayoutType value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.PreferAudioChannelLayout, value);

    partial void OnDefaultPlayerDisplayModeChanged(PlayerDisplayMode value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.DefaultPlayerDisplayMode, value);

    partial void OnPreferCodecChanged(PreferCodecType value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.PreferCodec, value);

    partial void OnPreferDecodeChanged(PreferDecodeType value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.PreferDecode, value);
        CheckMpvConfigVisible();
    }

    partial void OnIsCopyScreenshotChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.CopyAfterScreenshot, value);

    partial void OnIsOpenScreenshotFileChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.OpenAfterScreenshot, value);

    partial void OnPlayerSpeedEnhancementChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.IsPlayerSpeedEnhancement, value);

    partial void OnGlobalPlayerSpeedChanged(bool value)
    {
        if (!value)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.PlayerSpeed, 1.0d);
        }

        SettingsToolkit.WriteLocalSetting(SettingNames.IsPlayerSpeedShare, value);
    }

    partial void OnIsAutoPlayNextRecommendVideoChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.AutoPlayNextRecommendVideo, value);

    partial void OnPreferQualityChanged(PreferQualityType value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.PreferQuality, value);

    partial void OnHideWhenCloseWindowChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.HideWhenCloseWindow, value);

    partial void OnIsNotificationEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.IsNotificationEnabled, value);

    partial void OnIsVideoMomentNotificationEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.IsVideoMomentNotificationEnabled, value);

    partial void OnIsLiveStartNotificationEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.IsLiveStartNotificationEnabled, value);

    partial void OnIsReplyNotificationEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.IsReplyNotificationEnabled, value);

    partial void OnIsSeasonUpdateNotificationEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.IsSeasonUpdateNotificationEnabled, value);

    partial void OnBottomProgressVisibleChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.IsBottomProgressBarVisible, value);

    partial void OnNoP2PChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.PlayWithoutP2P, value);

    partial void OnCdnServerChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.PlayerCdnServer, value);

    partial void OnRememberPlaybackEnhancementsChanged(bool value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.RememberPlaybackEnhancements, value);
        if (!value)
        {
            // 关闭记忆时立刻清掉已保存的增强设置，避免下次启动又恢复.
            SettingsToolkit.WriteLocalSetting(SettingNames.Anime4KMode, Anime4KMode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.ArtCNNMode, ArtCNNMode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.Nnedi3Mode, Nnedi3Mode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.RavuMode, RavuMode.None);
            SettingsToolkit.WriteLocalSetting(SettingNames.NvidiaVsrScale, VsrScale.None);
        }
    }

    partial void OnPreferWebEngineChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.PlayerEngine, value ? PlayerEngineType.Web : PlayerEngineType.Mpv);

    partial void OnAutoFallbackToWebChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.PlayerAutoFallback, value);

    partial void OnProxyEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.ProxyEnabled, value);

    partial void OnProxyServerChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.ProxyServer, value);

    partial void OnProxyServerHkChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.ProxyServerHk, value);

    partial void OnProxyServerTwChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.ProxyServerTw, value);

    partial void OnProxyServerCnChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.ProxyServerCn, value);

    partial void OnReplaceCdnEnabledChanged(bool value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.ReplaceCdnEnabled, value); 
        if (value && CdnReplaceMode == CdnReplaceMode.None)
        {
            // 打开开关却停在「不替换」等于没开，这里顺手把模式推到仅替换 PCDN.
            CdnReplaceModeIndex = (int)CdnReplaceMode.PcdnOnly;
        }

        Serilog.Log.Information($"CDN 设置已保存：替换={value} 模式={CdnReplaceMode} 自动优选={CdnAutoSelect}");
    }

    partial void OnCdnReplaceModeChanged(CdnReplaceMode value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.CdnReplaceMode, value);
        Serilog.Log.Information("CDN 替换模式已保存：{Mode}", value);
    }

    partial void OnCdnAutoSelectChanged(bool value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.CdnAutoSelect, value);
        Serilog.Log.Information("CDN 自动优选已保存：{Value}", value);
    }

    partial void OnCdnFilterPortChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.CdnFilterPort, value);

    partial void OnCdnRewriteAkamaiChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.CdnRewriteAkamai, value);

    partial void OnCdnStallSwitchChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.CdnStallSwitch, value);

    partial void OnCdnServerSelectedChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == AutoCdnServerOption)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.CdnAutoSelect, true);
            return;
        }

        SettingsToolkit.WriteLocalSetting(SettingNames.PlayerCdnServer, value);
        SettingsToolkit.WriteLocalSetting(SettingNames.CdnAutoSelect, false);
    }

    private static int ResolveSponsorServerIndex(string server)
        => (server ?? string.Empty).TrimEnd('/') switch
        {
            "" or SponsorBlockService.FallbackServer => 0,
            "https://sponsor.ajay.app" => 1,
            "https://bsbsb.top" or "https://bsbsb.top/api" => 2,
            _ => 3,
        };

    partial void OnSponsorServerIndexChanged(int value)
    {
        var server = value switch
        {
            0 => SponsorBlockService.DefaultServer,
            1 => "https://sponsor.ajay.app",
            2 => "https://bsbsb.top",
            _ => string.IsNullOrWhiteSpace(SponsorServerCustom) ? SponsorBlockService.DefaultServer : SponsorServerCustom,
        };

        SettingsToolkit.WriteLocalSetting(SettingNames.SponsorBlockServer, server);
    }
    partial void OnSponsorServerCustomChanged(string value)
    {
        if (SponsorServerIndex == 3 && !string.IsNullOrWhiteSpace(value))
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.SponsorBlockServer, value);
        }
    }

    partial void OnSponsorManualModeChanged(bool value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.SponsorBlockManualMode, value);

    partial void OnSponsorMinDurationChanged(double value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.SponsorBlockMinDuration, value);

    partial void OnSponsorIncludeSelfPromoChanged(bool value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.SponsorBlockIncludeSelfPromo, value);

    partial void OnSponsorIncludeIntroChanged(bool value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.SponsorBlockIncludeIntro, value);

    partial void OnSponsorIncludeOutroChanged(bool value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.SponsorBlockIncludeOutro, value);

    partial void OnAdCloudEnabledChanged(bool value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.AdCloudEnabled, value);

    partial void OnAdCloudUrlChanged(string value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.AdCloudUrl, value);

    partial void OnAiInterestEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.AiInterestEnabled, value);

    partial void OnAiInterestMaxLengthChanged(int value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.AiInterestMaxLength, Math.Clamp(value, 20, 200));

    partial void OnAiAdEnabledChanged(bool value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.AiAdEnabled, value);

    partial void OnAiAdEndpointChanged(string value)
    {
        if (AiAutoSave)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.AiAdEndpoint, value);
        }
    }

    partial void OnAiAdKeyChanged(string value)
    {
        if (AiAutoSave)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.AiAdKey, value);
        }
    }

    partial void OnAiAdModelChanged(string value)
    {
        if (AiAutoSave)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.AiAdModel, value);
        }
    }

    partial void OnAiAdPromptChanged(string value)
    {
        if (AiAutoSave)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.AiAdPrompt, value);
        }
    }

    partial void OnAudioAdEnabledChanged(bool value)
    => SettingsToolkit.WriteLocalSetting(SettingNames.AudioAdEnabled, value);

    partial void OnDashScopeKeyChanged(string value)
    {
        if (AiAutoSave)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.DashScopeKey, value);
        }
    }

    partial void OnDashScopeModelChanged(string value)
    {
        if (AiAutoSave)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.DashScopeModel, value);
        }
    }

    [RelayCommand]
    private void ResetAiAdPrompt()
    {
        AiAdPrompt = AiAdAnalyzer.DefaultPrompt;
        AdSkipStatusText = "已恢复默认提示词";
    }

    partial void OnCdnReplaceModeIndexChanged(int value)
    {
        if (value < 0 || value > 3)
        {
            return;
        }

        CdnReplaceMode = (CdnReplaceMode)value;
    }

    partial void OnCdnServerIndexChanged(int value)
    {
        if (CdnServerCollection is null || value < 0 || value >= CdnServerCollection.Count)
        {
            return;
        }

        CdnServerSelected = CdnServerCollection[value];
    }

    [RelayCommand]
    private async Task TestCdnLatencyAsync()
    {
        CdnLatencyText = "测试中…";
        if (CdnPoolService.Count == 0)
        {
            await CdnPoolService.SyncAsync(this.Get<ILogger<SettingsPageViewModel>>());
            RebuildCdnServerList();
        }

        if (CdnPoolService.Count == 0)
        {
            CdnLatencyText = "节点池是空的，先点上面「刷新节点池」";
            Services.CdnStatusBus.Report("CDN 延迟测试：节点池为空，先刷新节点池");
            return;
        }

        var tasks = CdnPoolService.GetCandidates(16)
            .Select(async host =>
            {
                var ms = await CdnAccelerator.TestLatencyAsync(
                    host,
                    // 设置页拿不到可用样本：传空串，让 TestLatencyAsync 走 https://host/ + TCP 兜底.
                    string.Empty,
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                    "https://www.bilibili.com");
                return (host, ms);
            })
            .ToList();

        var results = await Task.WhenAll(tasks);
        var text = string.Join("　", results
            .OrderBy(p => p.ms < 0 ? int.MaxValue : p.ms)
            .Select(p => p.ms < 0 ? $"{p.host}: 不可用" : $"{p.host}: {p.ms}ms"));
        CdnLatencyText = text;
        var best = results.Where(p => p.ms >= 0).OrderBy(p => p.ms).FirstOrDefault();
        Services.CdnStatusBus.Report(best.host is null
            ? "CDN 延迟测试：全部节点不可用"
            : $"CDN 延迟测试完成，最快 {best.host}（{best.ms}ms）");
    }

    partial void OnDefaultDownloadPathChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.DownloadFolder, value);

    partial void OnDownloadWithDanmakuChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.DownloadWithDanmaku, value);

    partial void OnOpenFolderAfterDownloadChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.OpenFolderAfterDownload, value);

    partial void OnUseExternalBBDownChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.UseExternalBBDown, value);

    partial void OnOnlyCopyCommandWhenDownloadChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.OnlyCopyCommandWhenDownload, value);

    partial void OnWithoutCredentialWhenGenDownloadCommandChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.WithoutCredentialWhenGenDownloadCommand, value);

    partial void OnFilterAISubtitleChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.FilterAISubtitle, value);

    partial void OnPlayNextWithoutTipChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.PlayNextWithoutTip, value);

    partial void OnIsSponsorBlockEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.EnableSponsorBlock, value);

    partial void OnIsAcrylicBackdropEnabledChanged(bool value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.EnableAcrylicBackdrop, value);
        this.Get<AppViewModel>().ApplyBackdrop(value);
    }


    partial void OnMomentVideoLayoutPresetChanged(int value)
    {
        var safeValue = Math.Clamp(value, 0, 4);
        SettingsToolkit.WriteLocalSetting(SettingNames.MomentVideoLayoutPreset, safeValue);
        App.ApplyMomentVideoLayoutPreset(safeValue);
    }

    partial void OnMomentVideoOpenedMarkStyleChanged(MomentVideoOpenedMarkStyle value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.MomentVideoOpenedMarkStyle, value);
        this.Get<MomentPageViewModel>().SetVideoOpenedMarkStyle(value);
        this.Get<PopularPageViewModel>().SetVideoOpenedMarkStyle(value);
    }

    partial void OnMTCBehaviorChanged(MTCBehavior value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.MTCBehavior, value);

    partial void OnIsAIStreamingResponseChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.IsAIStreamingResponse, value);

    partial void OnUseWebPlayerWhenLiveChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.UseWebPlayerWhenLive, value);

    partial void OnShowSearchRecommendChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.ShowSearchRecommend, value);

    partial void OnIsPopularNavVisibleChanged(bool value)
        => WriteNavVisibleSetting(typeof(PopularPage), value);

    partial void OnIsMomentNavVisibleChanged(bool value)
        => WriteNavVisibleSetting(typeof(MomentPage), value);

    partial void OnIsVideoNavVisibleChanged(bool value)
        => WriteNavVisibleSetting(typeof(VideoPartitionPage), value);

    partial void OnIsLiveNavVisibleChanged(bool value)
        => WriteNavVisibleSetting(typeof(LivePartitionPage), value);

    partial void OnIsSearchHomeNavVisibleChanged(bool value)
        => WriteNavVisibleSetting(typeof(SearchHomePage), value);

    partial void OnIsAnimeNavVisibleChanged(bool value)
        => WriteNavVisibleSetting(typeof(AnimePage), value);

    partial void OnIsCinemaNavVisibleChanged(bool value)
        => WriteNavVisibleSetting(typeof(CinemaPage), value);

    partial void OnIsArticleNavVisibleChanged(bool value)
        => WriteNavVisibleSetting(typeof(ArticlePartitionPage), value);

    partial void OnScreenshotActionChanged(ScreenshotAction value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.ScreenshotAction, value);

    partial void OnScreenshotFolderPathChanged(string? value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.CustomScreenshotFolderPath, value ?? string.Empty);

    partial void OnMaxVolumeChanged(double value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.MaxVolume, value);

    partial void OnAudioExclusiveEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.AudioExclusiveEnabled, value);

    partial void OnMaxBackCacheSizeChanged(double value)
    {
        if (double.IsNaN(value))
        {
            return;
        }

        SettingsToolkit.WriteLocalSetting(SettingNames.MaxBackCacheSize, value);
    }

    partial void OnCacheOnDiskChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.CacheOnDisk, value);

    partial void OnScrollAccelerateChanged(bool value)
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.ScrollAccelerate, value);
        // 通知 Visor 更新滚动加速设置
        _ = this.Get<AppViewModel>().UpdateVisorScrollAccelerateAsync(value);
    }

    partial void OnTempPlaybackRateChanged(double value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.TempPlaybackRate, value);

    partial void OnD3D11AdapterNameChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.D3D11AdapterName, value);

    partial void OnVulkanDeviceNameChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.VulkanDeviceName, value);

    partial void OnQualityUnlockEnabledChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.QualityUnlockEnabled, value);

    partial void OnQualityUnlockAutoMaxChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.QualityUnlockAutoMax, value);

    partial void OnQualityUnlockServerChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.QualityUnlockServer, value ?? string.Empty);

    partial void OnQualityUnlockHomePageChanged(string value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.QualityUnlockHomePage, value ?? string.Empty);

    [RelayCommand]
    private async Task SyncQualityUnlockAsync()
    {
        QualityUnlockStatusText = "正在同步…";
        var result = await Services.QualityUnlockService.SyncAsync(this.Get<ILogger<SettingsPageViewModel>>());
        if (result.Success)
        {
            QualityUnlockServer = result.Server!;
            if (!string.IsNullOrEmpty(result.HomePage))
            {
                QualityUnlockHomePage = result.HomePage!;
            }

            QualityUnlockStatusText = $"已同步：{result.Server}（{Services.QualityUnlockService.LastSync:MM-dd HH:mm}）";
        }
        else
        {
            QualityUnlockStatusText = $"同步失败，继续使用当前服务器（{Services.QualityUnlockService.LastSync:MM-dd HH:mm} 尝试过）";
        }
    }

    [RelayCommand]
    private async Task RefreshCdnPoolAsync()
    {
        CdnPoolStatusText = "正在刷新节点池…";
        var logger = this.Get<ILogger<SettingsPageViewModel>>();
        var result = await CdnPoolService.SyncAsync(logger);
        CdnPoolStatusText = result.Success
            ? $"{result.Count} 个节点（{result.Source}）· 上次同步 {CdnPoolService.LastSync:MM-dd HH:mm}"
            : $"{CdnPoolService.StatusText}（刷新失败）";
        Services.CdnStatusBus.Report(result.Success
            ? $"CDN 节点池已刷新：{result.Count} 个（{result.Source}）"
            : "CDN 节点池刷新失败，继续用旧列表");
        RebuildCdnServerList();
    }
    partial void OnAiAutoSaveChanged(bool value)
        => SettingsToolkit.WriteLocalSetting(SettingNames.AiAutoSave, value);

    [RelayCommand]
    private async Task SaveAiSettingsAsync()
    {
        SettingsToolkit.WriteLocalSetting(SettingNames.AiAdEndpoint, AiAdEndpoint ?? string.Empty);
        SettingsToolkit.WriteLocalSetting(SettingNames.AiAdKey, AiAdKey ?? string.Empty);
        SettingsToolkit.WriteLocalSetting(SettingNames.AiAdModel, AiAdModel ?? string.Empty);
        SettingsToolkit.WriteLocalSetting(SettingNames.AiAdPrompt, AiAdPrompt ?? string.Empty);
        SettingsToolkit.WriteLocalSetting(SettingNames.DashScopeKey, DashScopeKey ?? string.Empty);
        SettingsToolkit.WriteLocalSetting(SettingNames.DashScopeModel, DashScopeModel ?? string.Empty);
        AiTestStatusText = $"已保存（{DateTime.Now:HH:mm:ss}）";

        // 音频分析用的是另一套配置（阿里云 Key + 转写模型），漏填要到播放时才暴露，
        // 所以保存时顺手核一遍.
        var (ok, message) = await AiEndpointTester.TestAudioAnalysisAsync(DashScopeKey, DashScopeModel);
        AiTestStatusText += ok
            ? $" · 音频分析可用：{message}"
            : $" · 音频分析不可用：{message}";
        if (!ok)
        {
            AdSkipStatusText = AiTestStatusText;
        }
    }

    [RelayCommand]
    private async Task TestAudioAnalysisAsync()
    {
        AiTestStatusText = "正在测试音频分析…";
        var (ok, message) = await AiEndpointTester.TestAudioAnalysisAsync(DashScopeKey, DashScopeModel);
        AiTestStatusText = (ok ? "音频分析可用：" : "音频分析有问题：") + message;
        AdSkipStatusText = AiTestStatusText;
    }

    [RelayCommand]
    private async Task TestAiApiAsync()
    {
        AiTestStatusText = "正在测试接口…";
        var (ok, message) = await AiEndpointTester.TestApiAsync(AiAdEndpoint, AiAdKey);
        AiTestStatusText = (ok ? "接口正常：" : "接口有问题：") + message;
        AdSkipStatusText = AiTestStatusText;
    }

    [RelayCommand]
    private async Task TestAiModelAsync()
    {
        AiTestStatusText = "正在测试模型…";
        var (ok, message) = await AiEndpointTester.TestModelAsync(AiAdEndpoint, AiAdKey, AiAdModel);
        AiTestStatusText = (ok ? "模型正常：" : "模型有问题：") + message;
        AdSkipStatusText = AiTestStatusText;
    }

    [RelayCommand]
    private async Task DetectAiModelsAsync()
    {
        AiModelDetectStatusText = "正在查询可用模型…";
        var (ok, message, models) = await AiEndpointTester.FetchModelsAsync(AiAdEndpoint, AiAdKey);
        AiModelOptions.Clear();
        if (!ok)
        {
            AiModelDetectStatusText = message;
            return;
        }

        foreach (var model in models)
        {
            AiModelOptions.Add(model);
        }

        AiModelDetectStatusText = models.Count > 0
            ? $"{message}，在下面挑一个，或继续手填"
            : message;

        // 已经填着的模型如果就在列表里，顺手选中，免得用户以为没查出来.
        if (models.Count > 0 && !string.IsNullOrWhiteSpace(AiAdModel))
        {
            var matched = models.FirstOrDefault(p => string.Equals(p, AiAdModel.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                AiModelPicked = matched;
            }
        }
    }

    partial void OnAiModelPickedChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            AiAdModel = value;
        }
    }
    /// <summary>
    /// 按当前节点池重建可选的 CDN 服务器列表.
    /// 第一项是「自动优选」，其余来自内置节点 + 抓到的节点池.
    /// </summary>
    public void RebuildCdnServerList()
    {
        CdnServerCollection.Clear();
        CdnServerCollection.Add(AutoCdnServerOption);

        foreach (var host in CdnPoolService.GetCandidates(60))
        {
            if (!CdnServerCollection.Contains(host))
            {
                CdnServerCollection.Add(host);
            }
        }

        if (!string.IsNullOrWhiteSpace(CdnServerSelected)
            && !CdnServerCollection.Contains(CdnServerSelected))
        {
            // 已保存的节点可能已经不在池子里，仍要能选中.
            CdnServerCollection.Add(CdnServerSelected);
        }

        var isAuto = SettingsToolkit.ReadLocalSetting(SettingNames.CdnAutoSelect, true);
        var index = isAuto ? 0 : CdnServerCollection.IndexOf(CdnServerSelected);
        CdnServerIndex = index < 0 ? 0 : index;
        Serilog.Log.Information(
            "CDN 可选节点已刷新：{Count} 个，当前选中 {Selected}",
            CdnServerCollection.Count,
            index >= 0 && index < CdnServerCollection.Count ? CdnServerCollection[index] : "(空)");
    }

}






