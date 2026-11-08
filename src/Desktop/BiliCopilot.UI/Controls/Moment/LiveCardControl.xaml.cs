// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using Windows.System;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 直播分区的一张卡片（Reactor 列的宿主控件）.
/// </summary>
public sealed partial class LiveCardControl : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveCardControl"/> class.
    /// </summary>
    public LiveCardControl() => InitializeComponent();

    /// <summary>
    /// 卡片视图模型.
    /// </summary>
    public LiveSectionCardViewModel ViewModel
    {
        get => (LiveSectionCardViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// ViewModel 依赖属性.
    /// </summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(LiveSectionCardViewModel), typeof(LiveCardControl), new PropertyMetadata(null));

    private void OnCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => ViewModel?.PlayCommand.Execute(default);

    private async void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } card && !string.IsNullOrEmpty(card.BrowserUrl))
        {
            await Launcher.LaunchUriAsync(new Uri(card.BrowserUrl));
        }
    }
}

