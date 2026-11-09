// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.Toolkits;
using Windows.Graphics;
using WinUIEx;

namespace BiliCopilot.UI.Forms;

/// <summary>
/// 主窗口.
/// </summary>
public sealed partial class MainWindow : WindowBase, ITipWindow
{
    private const int WindowMinWidth = 640;
    private const int WindowMinHeight = 480;
    private readonly MainShell _shell;
    private readonly ReactorHostControl _shellHost;
    private bool _isFirstActivated = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        Services.StartupTrace.Mark("MainWindow:ctor:begin");
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        Title = ResourceToolkit.GetLocalizedString(StringNames.AppName);
        this.SetIcon("Assets/logo.ico");
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        MinWidth = WindowMinWidth;
        MinHeight = WindowMinHeight;
        this.Get<AppViewModel>().Windows.Add(this);

        var navigationViewModel = this.Get<NavigationViewModel>();
        var mainFrame = new BiliCopilot.UI.Controls.MainFrame();
        var overlayFrame = new BiliCopilot.UI.Controls.OverlayFrame();
        navigationViewModel.Initialize(null, mainFrame, overlayFrame);
        Services.StartupTrace.Mark("MainWindow:ctor:navigation-init-done");
        _shell = new MainShell(navigationViewModel, mainFrame, overlayFrame, this.Get<BiliCopilot.UI.ViewModels.Components.AccountViewModel>());
        _shellHost = new ReactorHostControl();
        RootGrid.Children.Insert(0, _shellHost);
        Services.StartupTrace.Mark("MainWindow:ctor:host-added");
        _shellHost.Mount(_shell);
        Services.StartupTrace.Mark("MainWindow:ctor:shell-mounted");
        _shellHost.Loaded += OnShellHostLoaded;

        // Reactor 宿主与窗口的生命周期：用来区分「Reactor 重新渲染」和「XAML 控件被重造」.
        Services.PlayerLifecycle.Log("attach", "ReactorHost(MainShell)", $"hwnd={AppWindow.Id.Value}");
        _shellHost.Unloaded += (_, __) => Services.PlayerLifecycle.Log("detach", "ReactorHost(MainShell)", "Unloaded", withStack: true);
        ApplyBackdropSetting(SettingsToolkit.ReadLocalSetting(SettingNames.EnableAcrylicBackdrop, true));
        Services.StartupTrace.Mark("MainWindow:ctor:selected-item:begin");
        SelectedItem();
        Services.StartupTrace.Mark("MainWindow:ctor:selected-item:exit");
        Activated += OnActivated;
        Closed += OnClosed;
    }

    /// <inheritdoc/>
    public async Task ShowTipAsync(string text, InfoType type = InfoType.Error)
    {
        var popup = new TipPopup() { Text = text };
        TipContainer.Visibility = Visibility.Visible;
        TipContainer.Children.Add(popup);
        await popup.ShowAsync(type);
        TipContainer.Children.Remove(popup);
        TipContainer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 应用亚克力背景.
    /// </summary>
    /// <param name="enabled">是否启用.</param>
    public void ApplyBackdropSetting(bool enabled)
    {
        SystemBackdrop = enabled ? new DesktopAcrylicBackdrop() : null;

        // 亚克力没生效基本是三个原因：设置项被重置成 false、系统关了"透明效果"、
        // 或者窗口内容把背景整个盖住了。把判断结果记一笔，排查不用猜.
        var transparency = false;
        try
        {
            transparency = new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled;
        }
        catch
        {
        }

        Services.StartupTrace.Mark("MainWindow:backdrop", $"acrylic={enabled} transparency={transparency} backdrop={SystemBackdrop?.GetType().Name ?? "null"}");
    }

    private void SelectedItem()
    {
        var selected = this.Get<NavigationViewModel>().MenuItems.FirstOrDefault(p => p.IsSelected);
        selected?.NavigateCommand.Execute(default);
    }

    private void OnShellHostLoaded(object sender, RoutedEventArgs e)
    {
        var titleBar = _shell.TitleBarElement;
        if (titleBar is not null)
        {
            SetTitleBar(titleBar);
        }

        var keyboardSource = InputKeyboardSource.GetForIsland(_shellHost.XamlRoot?.ContentIsland);
        if (keyboardSource is not null)
        {
            keyboardSource.KeyUp += OnWindowKeyUp;
        }

        // 首屏内容挂上来 = 启动成功。不能等 Window.Activated：
        // 从自动化/后台方式拉起时窗口可能一直不激活，那样永远等不到激活事件.
        BiliCopilot.UI.Services.StartupTrace.Mark("MainWindow:shell-loaded");

        // 内容挂上了就等于启动成功：立刻清启动守卫，别等 6 秒延迟——
        // 那 6 秒里被杀掉的话，守卫会误判启动失败，下一轮把 Settings 挪走、偏好全丢.
        BiliCopilot.UI.App.NotifyStartupReady();
        this.Get<Microsoft.UI.Dispatching.DispatcherQueue>().TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => BiliCopilot.UI.Services.StartupTrace.MarkReady());
    }

    private static PointInt32 GetSavedWindowPosition()
    {
        var left = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowPositionLeft, 0);
        var top = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowPositionTop, 0);
        return new PointInt32(left, top);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            this.Get<AppViewModel>().ActivatedWindow = this;
        }

        if (!_isFirstActivated)
        {
            return;
        }

        BiliCopilot.UI.Services.StartupTrace.Mark("MainWindow:first-activated");

        // 亚克力在内容还没铺开时设上去会被后面的图层盖掉，首帧激活后再设一次.
        ApplyBackdropSetting(SettingsToolkit.ReadLocalSetting(SettingNames.EnableAcrylicBackdrop, true));
        MoveAndResize();
        this.Get<ILogger<App>>().LogInformation($"App version: {this.Get<IAppToolkit>().GetPackageVersion()}");
        var localTheme = SettingsToolkit.ReadLocalSetting(SettingNames.AppTheme, ElementTheme.Default);
        this.Get<AppViewModel>().ChangeThemeCommand.Execute(localTheme);
        AppWindow.Changed += OnWindowChanged;
        _isFirstActivated = false;

        // 再给一帧时间让首屏画出来，然后才认"启动成功"：Smoke Test 只认这个标记.
        this.Get<Microsoft.UI.Dispatching.DispatcherQueue>().TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => BiliCopilot.UI.Services.StartupTrace.MarkReady());
    }

    internal void UnregisterEventHandlers()
    {
        Activated -= OnActivated;
        Closed -= OnClosed;
        AppWindow.Changed -= OnWindowChanged;
    }

    private async void OnClosed(object sender, WindowEventArgs e)
    {
        var appVM = this.Get<AppViewModel>();
        var windows = appVM.Windows;
        var hasPlayer = appVM.Players.Count > 0;
        if (hasPlayer)
        {
            e.Handled = true;
            appVM.HideAllWindowsCommand.Execute(default);
            return;
        }

        appVM.IsClosed = true;
        this.Get<NavigationViewModel>().Back();
        foreach (var item in windows)
        {
            if (item is not MainWindow)
            {
                item.Close();
            }
        }

        foreach (var player in appVM.Players)
        {
            player.Close();
        }

        appVM.Windows.Remove(this);
        await this.Get<SettingsPageViewModel>().CheckSaveServicesAsync();
        UnregisterEventHandlers();
        SaveCurrentWindowStats();
        App.Current?.Exit();
        Environment.Exit(0);
    }

    private void OnWindowKeyUp(InputKeyboardSource sender, KeyEventArgs args)
    {
        _ = sender;
        _ = args;
    }

    private RectInt32 GetRenderRect(DisplayArea area)
    {
        var workArea = area.WorkArea;
        var scaleFactor = HwndExtensions.GetDpiForWindow(this.GetWindowHandle()) / 96d;
        var previousWidth = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowWidth, 1280d);
        var previousHeight = SettingsToolkit.ReadLocalSetting(SettingNames.MainWindowHeight, 760d);
        var width = Convert.ToInt32(previousWidth * scaleFactor);
        var height = Convert.ToInt32(previousHeight * scaleFactor);

        // 如果宽高比为 16 : 9，则计算出窗口实际宽度。
        double maxPageWidth;
        if (area.OuterBounds.Width * 9 == area.OuterBounds.Height * 16)
        {
            var maxWindowWidth = area.OuterBounds.Width / scaleFactor;
            maxPageWidth = maxWindowWidth - 74;
        }
        else
        {
            // 取当前高度的 16 : 9 比例宽度。
            var maxWindowWidth = area.OuterBounds.Height * 16 / 9 / scaleFactor;
            maxPageWidth = maxWindowWidth - 74;
        }

        App.Current.Resources["MaxPageWidth"] = maxPageWidth;

        // Ensure the window is not larger than the work area.
        if (height > workArea.Height - 20)
        {
            height = workArea.Height - 20;
        }

        var lastPoint = GetSavedWindowPosition();
        var isZeroPoint = lastPoint.X == 0 && lastPoint.Y == 0;
        var isValidPosition = lastPoint.X >= workArea.X && lastPoint.Y >= workArea.Y && lastPoint.X + width <= workArea.X + workArea.Width && lastPoint.Y + height <= workArea.Y + workArea.Height;
        var left = isZeroPoint || !isValidPosition
            ? (workArea.Width - width) / 2d
            : lastPoint.X;
        var top = isZeroPoint || !isValidPosition
            ? (workArea.Height - height) / 2d
            : lastPoint.Y;
        return new RectInt32(Convert.ToInt32(left), Convert.ToInt32(top), width, height);
    }

    private void MoveAndResize()
    {
        var lastPoint = GetSavedWindowPosition();
        var displayArea = DisplayArea.GetFromPoint(lastPoint, DisplayAreaFallback.Primary)
            ?? DisplayArea.Primary;
        var rect = GetRenderRect(displayArea);
        AppWindow.MoveAndResize(rect);
        var isMaximized = SettingsToolkit.ReadLocalSetting(SettingNames.IsMainWindowMaximized, false);
        if (isMaximized)
        {
            (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
        }
    }

    private void SaveCurrentWindowStats()
    {
        var left = AppWindow.Position.X;
        var top = AppWindow.Position.Y;
        var isMaximized = PInvoke.IsZoomed(new HWND(this.GetWindowHandle()));
        SettingsToolkit.WriteLocalSetting(SettingNames.IsMainWindowMaximized, (bool)isMaximized);

        if (!isMaximized)
        {
            SettingsToolkit.WriteLocalSetting(SettingNames.MainWindowPositionLeft, left);
            SettingsToolkit.WriteLocalSetting(SettingNames.MainWindowPositionTop, top);

            if (Height >= WindowMinHeight && Width >= WindowMinWidth)
            {
                SettingsToolkit.WriteLocalSetting(SettingNames.MainWindowHeight, Height);
                SettingsToolkit.WriteLocalSetting(SettingNames.MainWindowWidth, Width);
            }
        }
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange)
        {
            SaveCurrentWindowStats();
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint((UIElement)sender);
        if (point.Properties.IsXButton1Pressed || point.Properties.IsXButton2Pressed)
        {
            e.Handled = true;
            if (this.Get<NavigationViewModel>().IsOverlayOpen)
            {
                this.Get<NavigationViewModel>().Back();
            }
        }
    }
}












