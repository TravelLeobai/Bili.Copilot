// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Richasy.WinUIKernel.Share.Base;
using System.Collections.ObjectModel;
using Windows.System;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 直播分区（动态页新增的分段）.
/// 卡片区是手写瀑布流：按宽度决定列数、每列一个自动高度的 StackPanel，
/// 卡片高度跟内容走，标题两行也不会被裁.
/// </summary>
public sealed partial class LiveMomentSectionDetailControl : LiveSectionControlBase
{
    private const double MinCardWidth = 240d;
    private const double CardGap = 16d;
    private const double SidePadding = 32d;

    private readonly List<ObservableCollection<LiveSectionCardViewModel>> _buckets = [];
    private int _columnCount = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveMomentSectionDetailControl"/> class.
    /// </summary>
    public LiveMomentSectionDetailControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Items.CollectionChanged -= OnItemsChanged;
            ViewModel.Items.CollectionChanged += OnItemsChanged;
        }

        UpdateColumns(force: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Items.CollectionChanged -= OnItemsChanged;
        }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => Distribute();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width > 0 ? e.NewSize.Width : ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var available = Math.Max(MinCardWidth, width - SidePadding);
        var count = (int)Math.Max(1, Math.Floor((available + CardGap) / (MinCardWidth + CardGap)));
        if (count != _columnCount)
        {
            UpdateColumns(force: false);
        }
    }

    private void OnUperSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs e)
    {
        if (sender.SelectedItem is LiveSectionUperViewModel uper && ViewModel is not null)
        {
            ViewModel.SelectUper(uper);
        }
    }

    private void UpdateColumns(bool force)
    {
        if (ColumnHost is null)
        {
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : 1200;
        var available = Math.Max(MinCardWidth, width - SidePadding);
        var count = (int)Math.Max(1, Math.Floor((available + CardGap) / (MinCardWidth + CardGap)));
        if (!force && count == _columnCount && ColumnHost.Children.Count > 0)
        {
            return;
        }

        _columnCount = count;
        ColumnHost.ColumnDefinitions.Clear();
        ColumnHost.Children.Clear();
        _buckets.Clear();

        var template = Resources["LiveCardTemplate"] as DataTemplate;
        var panelTemplate = Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Spacing='16'/></ItemsPanelTemplate>") as ItemsPanelTemplate;

        for (var i = 0; i < count; i++)
        {
            var star = i < count - 1
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(1, GridUnitType.Star);
            ColumnHost.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            if (i > 0)
            {
                ColumnHost.ColumnDefinitions[i - 1].Width = new GridLength(1, GridUnitType.Star);
            }

            var bucket = new ObservableCollection<LiveSectionCardViewModel>();
            _buckets.Add(bucket);

            var items = new ItemsControl
            {
                ItemsSource = bucket,
                ItemTemplate = template,
                ItemsPanel = panelTemplate,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, i < count - 1 ? CardGap : 0, 0),
            };
            Grid.SetColumn(items, i);
            ColumnHost.Children.Add(items);
        }

        Distribute();
    }

    private void Distribute()
    {
        if (ViewModel is null || _buckets.Count == 0)
        {
            return;
        }

        foreach (var bucket in _buckets)
        {
            bucket.Clear();
        }

        for (var i = 0; i < ViewModel.Items.Count; i++)
        {
            _buckets[i % _buckets.Count].Add(ViewModel.Items[i]);
        }
    }

    private void OnFilterButtonClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement element)
        {
            return;
        }

        var type = (element.Tag as string) switch
        {
            "Live" => LiveFilterType.Live,
            "Replay" => LiveFilterType.Replay,
            "Clip" => LiveFilterType.Clip,
            _ => LiveFilterType.All,
        };

        ViewModel.SetFilterType(type);
        UpdateFilterButtons();
    }

    private void UpdateFilterButtons()
    {
        var current = ViewModel?.FilterType ?? LiveFilterType.All;
        Apply(FilterAllButton, current == LiveFilterType.All);
        Apply(FilterLiveButton, current == LiveFilterType.Live);
        Apply(FilterReplayButton, current == LiveFilterType.Replay);
        Apply(FilterClipButton, current == LiveFilterType.Clip);

        static void Apply(Button button, bool active)
        {
            if (button is null)
            {
                return;
            }

            button.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            button.Opacity = active ? 1 : 0.7;
        }
    }

    private async void OnManageClipCreatorsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var users = ViewModel.GetFollowedUsers();
        var panel = new StackPanel { Spacing = 6 };
        var checks = new List<(string Id, CheckBox Box)>();
        foreach (var pair in users.OrderBy(p => p.Value, StringComparer.CurrentCulture))
        {
            var box = new CheckBox
            {
                Content = pair.Value,
                IsChecked = ViewModel.ClipCreatorIds.Contains(pair.Key),
                Tag = pair.Key,
            };
            checks.Add((pair.Key, box));
            panel.Children.Add(box);
        }

        if (checks.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "还没拿到关注列表，先刷新一下动态页。", Opacity = 0.7 });
        }

        var dialog = new ContentDialog
        {
            Title = "选择做切片的 UP 主",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.SetClipCreators(checks.Where(p => p.Box.IsChecked == true).Select(p => p.Id));
            UpdateFilterButtons();
        }
    }
    private void OnCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is LiveSectionCardViewModel card)
        {
            card.PlayCommand.Execute(default);
        }
    }

    private async void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.DataContext is LiveSectionCardViewModel card
            && !string.IsNullOrEmpty(card.BrowserUrl))
        {
            await Launcher.LaunchUriAsync(new Uri(card.BrowserUrl));
        }
    }
}

/// <summary>
/// 直播分区控件基类.
/// </summary>
public abstract class LiveSectionControlBase : LayoutUserControlBase<LiveMomentSectionDetailViewModel>
{
}



