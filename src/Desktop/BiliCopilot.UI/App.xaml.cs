// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Forms;
using BiliCopilot.UI.Models;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Richasy.BiliKernel.Models.Media;
using Serilog;
using System.Text.Json;

namespace BiliCopilot.UI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private const string Id = "Richasy.BiliCopilot";
    private readonly DispatcherQueue _dispatcherQueue;
    private AppNotificationManager _notificationManager;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // 本机 IPv6 出口不通，.NET 默认优先 AAAA 会一直挂到超时；关掉 IPv6 优先，走 IPv4.
        AppContext.SetSwitch("System.Net.DisableIPv6", true);
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += OnUnhandledException;
        _notificationManager = AppNotificationManager.Default;
        _notificationManager.NotificationInvoked += OnAppNotificationInvoked;
        _notificationManager.Register();
    }

    /// <summary>
    /// 应用动态页面的视频排布预设.
    /// </summary>
    /// <param name="preset">预设索引.</param>
    public static void ApplyMomentVideoLayoutPreset(int preset)
    {
        double[] itemWidths = [260d, 320d, 400d, 500d, 640d];
        var safePreset = Math.Clamp(preset, 0, itemWidths.Length - 1);
        if (Current.Resources["MomentLayout"] is UniformGridLayout layout)
        {
            var itemWidth = itemWidths[safePreset];
            layout.MinItemWidth = itemWidth;
            layout.MinItemHeight = itemWidth;
        }
    }

    /// <summary>
    /// 日志文件夹.
    /// </summary>
    public static string LoggerFolder { get; private set; }

    private TaskbarIcon TrayIcon { get; set; }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var instance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey(Id);
        if (instance.IsCurrent)
        {
            var rootFolder = Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder;
            var fullPath = $"{rootFolder.Path}\\Logger";
            LoggerFolder = fullPath;
            if (!Directory.Exists(fullPath))
            {
                _ = Directory.CreateDirectory(fullPath);
            }

            instance.Activated += OnInstanceActivated;
            Richasy.WinUIKernel.Share.Base.ImageExBase.EnableBackgroundDecoding = false;
            GlobalDependencies.Initialize();
            ProbeEnhancementSettings();
            _ = SyncQualityUnlockIfNeededAsync();
            ApplyMomentVideoLayoutPreset(SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.MomentVideoLayoutPreset, 0));
            GlobalDependencies.Kernel.GetRequiredService<AppViewModel>().LaunchCommand.Execute(default);
        }
        else
        {
            var activatedArgs = instance.GetActivatedEventArgs();
            _ = instance.RedirectActivationToAsync(activatedArgs).AsTask().ContinueWith(_ => Current.Exit());
        }
    }

    private static MainWindow? GetMainWindow()
        => GlobalDependencies.Kernel.GetRequiredService<AppViewModel>().Windows.Find(p => p is MainWindow) as MainWindow;

    private void OnInstanceActivated(object? sender, AppActivationArguments e)
    {
        _dispatcherQueue.TryEnqueue(() => GetMainWindow()?.Activate());
    }

    private void InitializeTrayIcon()
    {
        if (TrayIcon != null)
        {
            return;
        }

        var showHideWindowCommand = (XamlUICommand)Resources["ShowHideWindowCommand"];
        showHideWindowCommand.ExecuteRequested += OnShowHideWindowCommandExecuteRequested;

        var exitApplicationCommand = (XamlUICommand)Resources["QuitCommand"];
        exitApplicationCommand.ExecuteRequested += OnQuitCommandExecuteRequested;

        try
        {
            TrayIcon = (TaskbarIcon)Resources["TrayIcon"];
            TrayIcon.ForceCreate();
        }
        catch (Exception)
        {
            var logger = Log.Logger.ForContext<App>();
            logger.Error("初始化任务栏图标失败");
        }
    }

    private void ExitApp()
    {
        TrayIcon?.Dispose();
        TrayIcon = null;
        _notificationManager?.Unregister();
        _notificationManager = null;
        Exit();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        this.Get<ILogger<App>>().LogError(e.Exception, "Unhandled exception occurred.");
        e.Handled = true;

        // 一旦出现布局循环检测异常，就删除上次选中的功能页，然后重启应用.
        if (e.Message.Contains("Layout cycle detected"))
        {
            SettingsToolkit.DeleteLocalSetting(Models.Constants.SettingNames.LastSelectedFeaturePage);
            this.Get<AppViewModel>().RestartCommand.Execute(default);
        }
    }

    private void OnAppNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("page", out var type))
        {
            var argsStr = args.Arguments.TryGetValue("args", out var a) ? a : string.Empty;
            if (string.IsNullOrEmpty(argsStr))
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                var identifier = JsonSerializer.Deserialize(argsStr, GlobalSerializeContext.Default.MediaIdentifier);
                if (type == "video")
                {
                    this.Get<AppViewModel>().OpenPlayerCommand.Execute(new MediaSnapshot(new VideoInformation(identifier, default)));
                }
                else if (type == "episode")
                {
                    this.Get<AppViewModel>().OpenPlayerCommand.Execute(new MediaSnapshot(default, new EpisodeInformation(identifier)));
                }
            });
        }
    }

    private void OnQuitCommandExecuteRequested(XamlUICommand sender, ExecuteRequestedEventArgs args)
        => ExitApp();

    private void OnShowHideWindowCommandExecuteRequested(XamlUICommand sender, ExecuteRequestedEventArgs args)
        => GetMainWindow()?.Activate();

    /// <summary>
    /// 启动时回读着色器/超分记忆，便于排查「退出重进后设置丢失」；同时支持一次性预置（诊断用）.
    /// </summary>
    private static void ProbeEnhancementSettings()
    {
        try
        {
            var seedDir = AppContext.BaseDirectory;
            try
            {
                seedDir = Package.Current.InstalledPath;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "非打包环境，回落执行目录");
            }

            var seedFile = Path.Combine(seedDir, "enhancement_seed.txt");
            if (File.Exists(seedFile))
            {
                var seed = File.ReadAllText(seedFile).Trim();
                if (!string.IsNullOrEmpty(seed))
                {
                    SettingsToolkit.WriteLocalSetting(SettingNames.Anime4KMode, seed);
                }

                File.Delete(seedFile);
            }

            var anime4k = SettingsToolkit.ReadLocalSetting(SettingNames.Anime4KMode, string.Empty);
            var artcnn = SettingsToolkit.ReadLocalSetting(SettingNames.ArtCNNMode, string.Empty);
            var nnedi3 = SettingsToolkit.ReadLocalSetting(SettingNames.Nnedi3Mode, string.Empty);
            var ravu = SettingsToolkit.ReadLocalSetting(SettingNames.RavuMode, string.Empty);
            var vsr = SettingsToolkit.ReadLocalSetting(SettingNames.NvidiaVsrScale, string.Empty);
            var remember = SettingsToolkit.ReadLocalSetting(SettingNames.RememberPlaybackEnhancements, true);
            Serilog.Log.Information(
                "启动读取播放增强记忆：记住={Remember} Anime4K={Anime4K} ArtCNN={ArtCNN} NNEDI3={Nnedi3} RAVU={Ravu} VSR={Vsr}",
                remember,
                anime4k,
                artcnn,
                nnedi3,
                ravu,
                vsr);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取播放增强记忆失败");
        }
    }

    /// <summary>
    /// 每天从上游同步一次画质解锁服务器（关闭功能或未到时间则跳过）.
    /// </summary>
    private static async Task SyncQualityUnlockIfNeededAsync()
    {
        try
        {
            if (!Services.QualityUnlockService.IsEnabled || !Services.QualityUnlockService.NeedSync)
            {
                return;
            }

            var logger = GlobalDependencies.Kernel.GetRequiredService<Microsoft.Extensions.Logging.ILogger<App>>();
            await Services.QualityUnlockService.SyncAsync(logger);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "画质解锁服务器同步失败");
        }
    }
}
