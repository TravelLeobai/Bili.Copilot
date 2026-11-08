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
using Richasy.BiliKernel.Models.Appearance;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;
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

        // 崩溃取证：未处理异常先落盘再让进程死，否则只能去事件查看器里看到一行 combase 码.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogFatal("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFatal("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        UnhandledException += (_, e) => LogFatal("Application.UnhandledException", e.Exception);

        // 实验开关：把 ContextFlyout 的初始化推迟到第一次真正打开时。
        // WinUI 认的是 AppContext 开关，名字就是 DeferContextFlyoutInit。
        // 打包应用从 shell 启动时拿不到父进程环境变量，所以除了环境变量
        // 还认一个标记文件，方便同一份二进制做开/关对比.
        DeferContextFlyoutEnabled = string.Equals(
            Environment.GetEnvironmentVariable("BILI_DEFER_CONTEXT_FLYOUT"),
            "1",
            StringComparison.Ordinal);
        if (!DeferContextFlyoutEnabled)
        {
            // App 构造阶段 ApplicationData.Current 可能还没就绪（会抛异常），
            // 所以直接用包族名拼路径，不依赖 WinRT 应用数据 API.
            foreach (var candidate in GetDeferFlagCandidates())
            {
                try
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        DeferContextFlyoutEnabled = true;
                        break;
                    }
                }
                catch
                {
                    // 单个候选路径读不了就跳过.
                }
            }
        }

        AppContext.SetSwitch("DeferContextFlyoutInit", DeferContextFlyoutEnabled);

        // 实验开关：控件默认样式的查找/应用优化（WinUI 内置开关，名字就是 DefaultStyleOptimizations）.
        // 和 DeferContextFlyoutInit 一样必须在 InitializeComponent 之前设置才生效.
        // 实验开关：DefaultStyleOptimizations 在本机 WinAppSDK 2.6.5 上会在启动恢复页面时崩（combase E_POINTER），先关掉.
        AppContext.SetSwitch("DefaultStyleOptimizations", false);

        InitializeComponent();
        Serilog.Log.Information("实验：DeferContextFlyoutInit={Enabled}", DeferContextFlyoutEnabled);
        Serilog.Log.Information("实验：DefaultStyleOptimizations=True");
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += OnUnhandledException;
        _notificationManager = AppNotificationManager.Default;
        _notificationManager.NotificationInvoked += OnAppNotificationInvoked;
        _notificationManager.Register();
    }

    /// <summary>
    /// 是否启用了 DeferContextFlyoutInit（埋点用来区分分组）.
    /// </summary>
    public static bool DeferContextFlyoutEnabled { get; private set; }

    /// <summary>
    /// 实验标记文件的候选路径：打包 LocalState 和运行目录都试一遍.
    /// App 构造阶段 ApplicationData.Current 可能还没就绪，所以这里不走 WinRT 应用数据 API.
    /// </summary>
    private static string[] GetDeferFlagCandidates()
    {
        var list = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            var family = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            list.Add(System.IO.Path.Combine(localAppData, "Packages", family, "LocalState", "defer-context-flyout.flag"));
        }
        catch
        {
            // 没包身份就算了.
        }

        list.Add(System.IO.Path.Combine(localAppData, "Packages", "Richasy.BiliCopilot.Dev_g7ew4fxhv1f46", "LocalState", "defer-context-flyout.flag"));
        list.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "defer-context-flyout.flag"));
        return [.. list];
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
        Services.StartupTrace.Mark("OnLaunched:enter", $"version={Services.StartupTrace.Version} sha={Services.StartupTrace.GitSha}");
        try
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

                Services.StartupTrace.Mark("OnLaunched:appdata-ready", rootFolder.Path);
                instance.Activated += OnInstanceActivated;
                // 后台解码：内核默认就是开的，这里恢复默认，解码不再压 UI 线程.
                Richasy.WinUIKernel.Share.Base.ImageExBase.EnableBackgroundDecoding = true;

                // 启动自愈：settings.dat 一旦被写坏，应用会每次启动都在同一个位置崩，
                // 外面只能靠卸载重装救。这里记一个"正在启动"的标记：
                // 连续两次启动都没走到成功，就把 Settings 挪走、用全新设置继续启动.
                EnsureHealthySettings(rootFolder.Path);
                ScheduleStartupMark(rootFolder.Path);
                Services.StartupTrace.Mark("OnLaunched:settings-checked");
                BiliCopilot.UI.Services.ImageDiagnostics.Initialize();
                Services.StartupTrace.Mark("OnLaunched:image-diag-ready");
                GlobalDependencies.Initialize();
                Services.StartupTrace.Mark("OnLaunched:dependencies-ready");
                ProbeEnhancementSettings();
                Services.StartupTrace.Mark("OnLaunched:enhancement-settings-read");
                _ = SyncQualityUnlockIfNeededAsync();
                _ = SyncCdnPoolIfNeededAsync();
                ApplyMomentVideoLayoutPreset(SettingsToolkit.ReadLocalSetting(Models.Constants.SettingNames.MomentVideoLayoutPreset, 0));
                Services.StartupTrace.Mark("OnLaunched:launch-command");
                GlobalDependencies.Kernel.GetRequiredService<AppViewModel>().LaunchCommand.Execute(default);
                Services.StartupTrace.Mark("OnLaunched:launch-command-returned");
            }
            else
            {
                Services.StartupTrace.Mark("OnLaunched:redirect-to-primary");
                var activatedArgs = instance.GetActivatedEventArgs();
                _ = instance.RedirectActivationToAsync(activatedArgs).AsTask().ContinueWith(_ => Current.Exit());
            }
        }
        catch (Exception ex)
        {
            Services.StartupTrace.Fail("OnLaunched:exception", ex);
            throw;
        }
    }

    private static MainWindow? GetMainWindow()
        => GlobalDependencies.Kernel.GetRequiredService<AppViewModel>().Windows.Find(p => p is MainWindow) as MainWindow;

    /// <summary>
    /// 把致命异常写到日志目录，方便下次崩溃时定位（事件查看器里只有 combase 的错误码）.
    /// </summary>
    /// <param name="source">来源标记.</param>
    /// <param name="exception">异常.</param>
    private static void LogFatal(string source, Exception? exception)
    {
        try
        {
            var folder = string.IsNullOrEmpty(LoggerFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliCopilot.Crash")
                : LoggerFolder;
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"crash-{DateTime.Now:yyyy-MM-dd}.txt");
            var text = $"{DateTime.Now:O} [{source}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(file, text);
        }
        catch
        {
            // 记不下来就算了，别在崩溃路径上再抛一次.
        }
    }

    /// <summary>
    /// 启动自愈：连续两次没走到"成功启动"就把 Settings 挪走。
    /// 坏掉的 settings.dat 会让应用每次启动都崩在 MainShell 构造里（combase 0x80004003）。
    /// </summary>
    /// <param name="localFolder">LocalState 目录.</param>
    private static void EnsureHealthySettings(string localFolder)
    {
        try
        {
            var guard = Path.Combine(localFolder, "startup-guard.txt");
            var attempts = File.Exists(guard) && int.TryParse(File.ReadAllText(guard).Trim(), out var value) ? value : 0;
            attempts++;
            if (attempts >= 2)
            {
                var packagesRoot = Directory.GetParent(localFolder)?.FullName;
                var settingsDir = packagesRoot is null ? null : Path.Combine(packagesRoot, "Settings");
                if (settingsDir is not null && Directory.Exists(settingsDir))
                {
                    var broken = Path.Combine(packagesRoot!, $"Settings-broken-{DateTime.Now:HHmmss}");
                    try
                    {
                        Directory.Move(settingsDir, broken);
                    }
                    catch
                    {
                        // 挪不动就算了，下一轮再试.
                    }
                }

                attempts = 1;
            }

            File.WriteAllText(guard, attempts.ToString());
        }
        catch
        {
            // 自愈本身失败不影响启动.
        }
    }

    /// <summary>
    /// 启动成功（窗口激活后调用）：清掉启动标记.
    /// </summary>
    private static void MarkStartupSucceeded(string localFolder)
    {
        try
        {
            var guard = Path.Combine(localFolder, "startup-guard.txt");
            if (File.Exists(guard))
            {
                File.Delete(guard);
            }
        }
        catch
        {
            // 忽略.
        }
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments e)
    {
        _dispatcherQueue.TryEnqueue(() => GetMainWindow()?.Activate());
    }

    /// <summary>
    /// 窗口起来之后再清启动标记：只有真的跑到这儿才算"启动成功".
    /// </summary>
    /// <param name="localFolder">LocalState 目录.</param>
    private void ScheduleStartupMark(string localFolder)
        => _dispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => MarkStartupSucceeded(localFolder)));

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

            // 开播提醒带的是 "房间号|主播uid|主播名|头像|标题"，直接开播放器进直播间.
            if (type == "live")
            {
                var parts = argsStr.Split('|', 5);
                if (parts.Length < 1 || !long.TryParse(parts[0], out var roomId) || roomId <= 0)
                {
                    return;
                }

                _dispatcherQueue.TryEnqueue(() =>
                {
                    var title = parts.Length > 4 ? parts[4] : string.Empty;
                    var identifier = new MediaIdentifier(roomId.ToString(), title, default);

                    // 带上主播信息，直播面板的头像/昵称/粉丝数才有东西显示.
                    UserProfile? user = null;
                    if (parts.Length > 3 && Uri.TryCreate(parts[3], UriKind.Absolute, out var faceUri))
                    {
                        var userId = parts.Length > 1 ? parts[1] : string.Empty;
                        var userName = parts.Length > 2 ? parts[2] : string.Empty;
                        user = new UserProfile(userId, userName, new BiliImage(faceUri));
                    }

                    var live = new LiveInformation(identifier, user ?? default!);
                    this.Get<AppViewModel>().OpenPlayerCommand.Execute(new MediaSnapshot(live));
                });
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

    /// <summary>
    /// 每天刷新一次 CDN 节点池（关闭 CDN 替换或未到时间则跳过）.
    /// </summary>
    private static async Task SyncCdnPoolIfNeededAsync()
    {
        try
        {
            if (!Services.CdnAccelerator.IsEnabled || !Services.CdnPoolService.NeedSync)
            {
                return;
            }

            var logger = GlobalDependencies.Kernel.GetRequiredService<Microsoft.Extensions.Logging.ILogger<App>>();
            await Services.CdnPoolService.SyncAsync(logger);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "CDN 节点池同步失败");
        }
    }
}






