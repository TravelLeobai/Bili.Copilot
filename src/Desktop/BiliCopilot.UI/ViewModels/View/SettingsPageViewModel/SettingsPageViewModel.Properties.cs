// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using Richasy.MpvKernel;
using Richasy.MpvKernel.Core.Enums;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 设置页面视图模型.
/// </summary>
public sealed partial class SettingsPageViewModel
{
    private bool _isInitialized;
    private string? _initialCustomLibMpvPath;

    [ObservableProperty]
    public partial bool IsRestartTipShown { get; set; }

    [ObservableProperty]
    private ElementTheme _appTheme;

    [ObservableProperty]
    private string _appThemeText;


    [ObservableProperty]
    private bool _isAcrylicBackdropEnabled;

    [ObservableProperty]
    private bool _isSponsorBlockEnabled;

    [ObservableProperty]
    private int _momentVideoLayoutPreset;

    [ObservableProperty]
    private MomentVideoOpenedMarkStyle _momentVideoOpenedMarkStyle;

    [ObservableProperty]
    private List<MomentVideoOpenedMarkStyle> _momentVideoOpenedMarkStyleCollection;

    [ObservableProperty]
    private bool _isAutoPlayNextRecommendVideo;

    [ObservableProperty]
    private PlayerDisplayMode _defaultPlayerDisplayMode;

    [ObservableProperty]
    private bool _playNextWithoutTip;

    [ObservableProperty]
    private bool _endWithPlaylist;

    [ObservableProperty]
    private bool _isMpvCustomOptionVisible;

    [ObservableProperty]
    private PreferCodecType _preferCodec;

    [ObservableProperty]
    private PreferQualityType _preferQuality;

    [ObservableProperty]
    private PreferDecodeType _preferDecode;

    [ObservableProperty]
    private MTCBehavior _mTCBehavior;

    [ObservableProperty]
    private bool _playerSpeedEnhancement;

    [ObservableProperty]
    private bool _globalPlayerSpeed;

    [ObservableProperty]
    private string _packageVersion;

    [ObservableProperty]
    private string _copyright;

    [ObservableProperty]
    private bool _isCopyScreenshot;

    [ObservableProperty]
    private bool _isOpenScreenshotFile;

    [ObservableProperty]
    private bool _hideWhenCloseWindow;

    [ObservableProperty]
    private bool _isNotificationEnabled;

    [ObservableProperty]
    private bool _isVideoMomentNotificationEnabled;


    [ObservableProperty]
    private bool _isSeasonUpdateNotificationEnabled;

    [ObservableProperty]
    private bool _isLiveStartNotificationEnabled;

    [ObservableProperty]
    private bool _isReplyNotificationEnabled;

    [ObservableProperty]
    private bool _bottomProgressVisible;

    [ObservableProperty]
    private bool _noP2P;

    [ObservableProperty]
    private string _cdnServer;

    [ObservableProperty]
    private bool _qualityUnlockEnabled;

    [ObservableProperty]
    private UnlockQualityPreference _unlockQualityPreference = UnlockQualityPreference.AutoHighest;


    [ObservableProperty]
    private string _qualityUnlockServer = string.Empty;

    [ObservableProperty]
    private string _qualityUnlockHomePage = string.Empty;


    [ObservableProperty]
    private string _qualityUnlockStatusText = string.Empty;

    [ObservableProperty]
    private bool _isQualityUnlockExpanded;
    [ObservableProperty]
    private bool _rememberPlaybackEnhancements = true;

    [ObservableProperty]
    private bool _preferWebEngine;

    [ObservableProperty]
    private bool _autoFallbackToWeb;
    [ObservableProperty]
    private bool _proxyEnabled;

    [ObservableProperty]
    private string _proxyServer;

    [ObservableProperty]
    private string _proxyServerHk;

    [ObservableProperty]
    private string _proxyServerTw;

    [ObservableProperty]
    private string _proxyServerCn;

    /// <summary>
    /// 媒体字节范围代理（本机回环 Range 代理 + 字节缓存），默认关闭.
    /// </summary>
    [ObservableProperty]
    private bool _prefetchProxyEnabled;

    [ObservableProperty]
    private bool _replaceCdnEnabled;

    [ObservableProperty]
    private CdnReplaceMode _cdnReplaceMode;

    [ObservableProperty]
    private bool _cdnAutoSelect;

    [ObservableProperty]
    private bool _cdnFilterPort;

    [ObservableProperty]
    private bool _cdnRewriteAkamai;

    [ObservableProperty]
    private bool _cdnStallSwitch;

    [ObservableProperty]
    private int _cdnReplaceModeIndex;

    [ObservableProperty]
    private int _cdnServerIndex;

    [ObservableProperty]
    private int _sponsorServerIndex;

    [ObservableProperty]
    private string _sponsorServerCustom;

    [ObservableProperty]
    private bool _sponsorManualMode;

    [ObservableProperty]
    private double _sponsorMinDuration;

    [ObservableProperty]
    private bool _sponsorIncludeSelfPromo;

    [ObservableProperty]
    private bool _sponsorIncludeIntro;

    [ObservableProperty]
    private bool _sponsorIncludeOutro;

    [ObservableProperty]
    private bool _adCloudEnabled;

    [ObservableProperty]
    private string _adCloudUrl;

    [ObservableProperty]
    private bool _aiAdEnabled;

    [ObservableProperty]
    private bool _aiInterestEnabled = true;


    /// <summary>
    /// AI 分析正文的字数上限（20~200）.
    /// </summary>
    [ObservableProperty]
    private int _aiInterestMaxLength = 60;

    [ObservableProperty]
    private bool _aiAutoSave = true;

    [ObservableProperty]
    private string _aiTestStatusText;

    [ObservableProperty]
    private string _aiAdEndpoint;

    [ObservableProperty]
    private string _aiAdKey;

    [ObservableProperty]
    private string _aiAdModel;

    /// <summary>
    /// 联网查到的可用模型列表（点「检测可用模型」后填充）.
    /// </summary>
    public ObservableCollection<string> AiModelOptions { get; } = [];

    /// <summary>
    /// 下拉里选中的模型，选中即写回 <see cref="AiAdModel"/>.
    /// </summary>
    [ObservableProperty]
    private string _aiModelPicked;

    /// <summary>
    /// 检测模型列表的过程状态.
    /// </summary>
    [ObservableProperty]
    private string _aiModelDetectStatusText;

    [ObservableProperty]
    private string _aiAdPrompt;

    [ObservableProperty]
    private bool _audioAdEnabled;

    [ObservableProperty]
    private string _dashScopeKey;

    [ObservableProperty]
    private string _dashScopeModel;

    [ObservableProperty]
    private string _adSkipStatusText;
    [ObservableProperty]
    private string _cdnServerSelected;

    [ObservableProperty]
    private string _cdnLatencyText;
    [ObservableProperty]
    private string _cdnPoolStatusText;

    /// <summary>
    /// CDN 替换模式可选项.
    /// </summary>
    public List<CdnReplaceMode> CdnReplaceModeCollection { get; set; } = new();

    /// <summary>
    /// 候选 CDN 服务器.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<string> CdnServerCollection { get; } = new();

    /// <summary>
    /// CDN 节点延迟测试结果（按延迟升序，不可用的排最后）.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<ViewModels.Items.CdnLatencyItem> CdnLatencyItems { get; } = new();

    [ObservableProperty]
    private string _defaultDownloadPath;

    [ObservableProperty]
    private bool _downloadWithDanmaku;

    [ObservableProperty]
    private bool _openFolderAfterDownload;

    [ObservableProperty]
    private bool _useExternalBBDown;

    [ObservableProperty]
    private bool _onlyCopyCommandWhenDownload;

    [ObservableProperty]
    private bool _withoutCredentialWhenGenDownloadCommand;

    [ObservableProperty]
    private bool _filterAISubtitle;

    [ObservableProperty]
    private bool _isAIStreamingResponse;

    [ObservableProperty]
    private bool _isWebDavEnabled;

    [ObservableProperty]
    private bool _isWebDavEmpty;

    [ObservableProperty]
    private List<PlayerDisplayMode> _playerDisplayModeCollection;

    [ObservableProperty]
    private List<PreferCodecType> _preferCodecCollection;

    [ObservableProperty]
    private List<PreferQualityType> _preferQualityCollection;

    /// <summary>
    /// 画质偏好的可选项（自动最高 / 1080P60 / 1080P 高码率）.
    /// </summary>
    [ObservableProperty]
    private List<UnlockQualityPreference> _unlockQualityPreferenceCollection = [];

    [ObservableProperty]
    private List<PreferDecodeType> _preferDecodeCollection;

    [ObservableProperty]
    private List<ExternalPlayerType> _externalPlayerTypeCollection;

    [ObservableProperty]
    private List<MTCBehavior> _mTCBehaviorCollection;

    [ObservableProperty]
    private bool _useWebPlayerWhenLive;

    [ObservableProperty]
    private bool _showSearchRecommend;

    [ObservableProperty]
    private bool _isPopularNavVisible;

    [ObservableProperty]
    private bool _isMomentNavVisible;

    [ObservableProperty]
    private bool _isVideoNavVisible;

    [ObservableProperty]
    private bool _isLiveNavVisible;

    [ObservableProperty]
    private bool _isSearchHomeNavVisible;

    [ObservableProperty]
    private bool _isAnimeNavVisible;

    [ObservableProperty]
    private bool _isCinemaNavVisible;

    [ObservableProperty]
    private bool _isArticleNavVisible;

    [ObservableProperty]
    private bool _isTopNavShown;

    [ObservableProperty]
    public partial ScreenshotAction ScreenshotAction { get; set; }

    [ObservableProperty]
    public partial string? ScreenshotFolderPath { get; set; }

    [ObservableProperty]
    public partial double TempPlaybackRate { get; set; }

    [ObservableProperty]
    public partial bool ScrollAccelerate { get; set; }

    [ObservableProperty]
    public partial bool CacheOnDisk { get; set; }

    [ObservableProperty]
    public partial string? CacheDirDescription { get; set; }

    [ObservableProperty]
    public partial bool HasCustomCacheDir { get; set; }

    [ObservableProperty]
    public partial bool AudioExclusiveEnabled { get; set; }

    [ObservableProperty]
    public partial string? CustomLibMpvPath { get; set; }

    [ObservableProperty]
    public partial bool CanResetLibMpvPath { get; set; }

    [ObservableProperty]
    public partial bool IsMpvExtraSettingExpanded { get; set; }

    [ObservableProperty]
    public partial double MaxVolume { get; set; }

    [ObservableProperty]
    public partial HrSeekType HrSeek { get; set; }

    [ObservableProperty]
    public partial double MaxCacheSize { get; set; }

    [ObservableProperty]
    public partial double MaxBackCacheSize { get; set; }

    [ObservableProperty]
    public partial double MaxCacheSeconds { get; set; }

    /// <summary>
    /// 「网络缓存」是否跟随 mpv 默认（打开后不下发前向缓存与预读秒数）.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCacheManualMode))]
    public partial bool CacheAutoMode { get; set; }

    /// <summary>
    /// 前向缓存 / 预读秒数是否可以手动填（跟着 <see cref="CacheAutoMode"/> 取反）.
    /// </summary>
    public bool IsCacheManualMode => !CacheAutoMode;

    /// <summary>
    /// cache-secs 上限（秒）；0 表示不限制、不下发.
    /// </summary>
    [ObservableProperty]
    public partial double CacheSecsLimit { get; set; }

    /// <summary>
    /// 前后向缓存互相借用（demuxer-donate-buffer）.
    /// </summary>
    [ObservableProperty]
    public partial bool DemuxerDonateBuffer { get; set; }

    /// <summary>
    /// 允许在已缓存范围内直接跳（demuxer-seekable-cache）.
    /// </summary>
    [ObservableProperty]
    public partial bool DemuxerSeekableCache { get; set; }

    /// <summary>
    /// 「缓冲与缓存」这一块是否展开.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCacheAdvancedExpanded { get; set; }

    [ObservableProperty]
    public partial bool UseIntegrationWhenSinglePlayWindow { get; set; }

    [ObservableProperty]
    public partial MpvBuiltInProfile MpvBuiltInProfile { get; set; }

    [ObservableProperty]
    public partial string TotalImageCacheSize { get; set; }

    [ObservableProperty]
    public partial AudioChannelLayoutType PreferAudioChannelLayout { get; set; }

    [ObservableProperty]
    public partial bool IsTotalImageCacheClearEnabled { get; set; }

    [ObservableProperty]
    public partial double StepForwardSecond { get; set; }

    [ObservableProperty]
    public partial double StepBackwardSecond { get; set; }

    [ObservableProperty]
    public partial MpvLogLevel LogLevel { get; set; }

    [ObservableProperty]
    public partial bool HideMainWindowOnPlay { get; set; }

    /// <summary>
    /// 屏蔽 B 站等级小于 3 的用户的评论与直播弹幕.
    /// </summary>
    [ObservableProperty]
    public partial bool BlockLowLevelUsers { get; set; }

    /// <summary>
    /// 弹幕时间戳跳转提示.
    /// </summary>
    [ObservableProperty]
    public partial bool DanmakuJumpEnabled { get; set; }

    [ObservableProperty]
    public partial string D3D11AdapterName { get; set; }

    [ObservableProperty]
    public partial string VulkanDeviceName { get; set; }

    /// <summary>
    /// GPU设备列表.
    /// </summary>
    public ObservableCollection<string> GpuDevices { get; } = [];





}

