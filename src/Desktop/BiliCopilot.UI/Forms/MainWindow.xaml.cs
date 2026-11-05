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
        InitializeComponent();
        // 不再扩展内容到标题栏：自定义标题栏会把左上按钮划进非客户区，
        // 指针事件进不来，折叠按钮点不动。这里退回系统标题栏，按钮就能正常点.
        ExtendsContentIntoTitleBar = false;
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
        _shell = new MainShell(navigationViewModel, mainFrame, overlayFrame, this.Get<BiliCopilot.UI.ViewModels.Components.AccountViewModel>());
        _shellHost = new ReactorHostControl();
        RootGrid.Children.Insert(0, _shellHost);
        _shellHost.Mount(_shell);
        _shellHost.Loaded += OnShellHostLoaded;
        ApplyBackdropSetting(SettingsToolkit.ReadLocalSetting(SettingNames.EnableAcrylicBackdrop, true));
        SelectedItem();
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
    }

    private void SelectedItem()
    {
        var selected = this.Get<NavigationViewModel>().MenuItems.FirstOrDefault(p => p.IsSelected);
        selected?.NavigateCommand.Execute(default);
    }

    private void OnShellHostLoaded(object sender, RoutedEventArgs e)
    {
        // 系统标题栏方案下不需要再指定拖拽区域.
        var keyboardSource = InputKeyboardSource.GetForIsland(_shellHost.XamlRoot?.ContentIsland);
        if (keyboardSource is not null)
        {
            keyboardSource.KeyUp += OnWindowKeyUp;
        }
    }

    /// <summary>
    /// 把标题栏上的按钮申报成非客户区的穿透区域。
    /// SetTitleBar 之后整条标题栏都算拖拽区，按钮拿不到指针事件（连 hover 都没有），
    /// 这里把按钮的矩形显式排除掉.
    /// </summary>
    private void UpdateTitleBarPassthrough(FrameworkElement titleBar)
    {
        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            var scale = HwndExtensions.GetDpiForWindow(this.GetWindowHandle()) / 96d;
            var rects = new List<RectInt32>();

            foreach (var button in _shell.TitleBarButtons)
            {
                if (button.Visibility != Visibility.Visible || button.ActualWidth <= 0 || button.ActualHeight <= 0)
                {
                    continue;
                }

                var bounds = button.TransformToVisual(titleBar).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                rects.Add(new RectInt32(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale),
                    (int)Math.Round(bounds.Height * scale)));
            }

            source.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "设置标题栏按钮穿透区域失败");
        }
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

        MoveAndResize();
        this.Get<ILogger<App>>().LogInformation($"App version: {this.Get<IAppToolkit>().GetPackageVersion()}");
        var localTheme = SettingsToolkit.ReadLocalSetting(SettingNames.AppTheme, ElementTheme.Default);
        this.Get<AppViewModel>().ChangeThemeCommand.Execute(localTheme);
        AppWindow.Changed += OnWindowChanged;
        _isFirstActivated = false;
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




