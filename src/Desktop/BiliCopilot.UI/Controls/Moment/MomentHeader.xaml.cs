// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.View;
using Richasy.WinUIKernel.Share.Base;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 动态头部.
/// </summary>
public sealed partial class MomentHeader : MomentHeaderBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MomentHeader"/> class.
    /// </summary>
    public MomentHeader() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        Selector.SelectionChanged += OnSelectorChanged;
        ViewModel.Initialized += OnViewModelInitialized;
        InitializeChildPartitions();
    }

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        if (ViewModel is not null)
        {
            ViewModel.Initialized -= OnViewModelInitialized;
        }

        Selector.SelectionChanged -= OnSelectorChanged;
    }

    private static string GetSectionName(MomentSectionType type)
        => type switch
        {
            MomentSectionType.Video => ResourceToolkit.GetLocalizedString(StringNames.Video),
            MomentSectionType.Comprehensive => ResourceToolkit.GetLocalizedString(StringNames.Comprehensive),
            MomentSectionType.Live => ResourceToolkit.GetLocalizedString(StringNames.Live),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    private void OnViewModelInitialized(object? sender, EventArgs e)
        => InitializeChildPartitions();

    private async void OnAddClipCreatorClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSection is not LiveMomentSectionDetailViewModel live)
        {
            return;
        }

        var users = live.GetFollowedUsers();
        var panel = new StackPanel { Spacing = 4 };
        var checks = new List<(string Id, CheckBox Box)>();
        foreach (var user in users.OrderBy(p => p.Name, StringComparer.CurrentCulture))
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 复选框在最左，然后头像，最后名字.
            var box = new CheckBox
            {
                Tag = user.Id,
                IsChecked = live.ClipCreatorIds.Contains(user.Id),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0,
            };

            var avatar = new BasicCoverImage
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                DecodeWidth = 80,
                Source = user.Avatar,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = user.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            row.Children.Add(box);
            Grid.SetColumn(avatar, 1);
            row.Children.Add(avatar);
            Grid.SetColumn(label, 2);
            row.Children.Add(label);
            checks.Add((user.Id, box));
            panel.Children.Add(row);
        }

        if (checks.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "还没拿到关注列表，先刷新一下动态页。", Opacity = 0.7 });
        }

        var dialog = new ContentDialog
        {
            Title = "选择做切片的 UP 主",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            live.SetClipCreators(checks.Where(p => p.Box.IsChecked == true).Select(p => p.Id));
        }
    }

    private void OnSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null)
        {
            return;
        }

        var item = sender.SelectedItem.Tag as IMomentSectionDetailViewModel;
        if (item is not null && item != ViewModel.SelectedSection)
        {
            ViewModel.SelectSectionCommand.Execute(item);
        }
    }

    private void InitializeChildPartitions()
    {
        Selector.Items.Clear();
        if (ViewModel.Sections is not null)
        {
            foreach (var item in ViewModel.Sections)
            {
                Selector.Items.Add(new SelectorBarItem()
                {
                    Text = GetSectionName(item.SectionType),
                    Tag = item,
                });
            }
        }

        Selector.SelectedItem = Selector.Items.FirstOrDefault(p => p.Tag == ViewModel.SelectedSection);
    }
}

/// <summary>
/// 动态头部基类.
/// </summary>
public abstract class MomentHeaderBase : LayoutUserControlBase<MomentPageViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MomentHeaderBase"/> class.
    /// </summary>
    protected MomentHeaderBase() => ViewModel = this.Get<MomentPageViewModel>();
}


