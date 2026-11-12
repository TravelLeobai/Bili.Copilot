// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.Controls.Player;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Richasy.BiliKernel.Models.Media;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 视频合集（分 P）面板.
/// 列表部分用 Reactor 重画，顶部的「仅显示序号」开关沿用原 XAML 控件.
/// </summary>
public sealed class VideoPartsPanel : Component
{
    private readonly VideoPlayerPartSectionDetailViewModel _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPartsPanel"/> class.
    /// </summary>
    public VideoPartsPanel(VideoPlayerPartSectionDetailViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var viewModel = UseObservableTree(_viewModel);

        var toggle = new XamlHostElement(
            () => new ToggleSwitch
            {
                OffContent = ResourceToolkit.GetLocalizedString(StringNames.OnlyIndex),
                OnContent = ResourceToolkit.GetLocalizedString(StringNames.OnlyIndex),
                Margin = new Thickness(12, 8, 12, 0),
                IsOn = viewModel.OnlyIndex,
            },
            element =>
            {
                var control = (ToggleSwitch)element;
                control.IsOn = viewModel.OnlyIndex;
                control.Toggled += OnToggleToggled;
            })
            .WithKey("parts-toggle");

        Element body = viewModel.OnlyIndex
            ? BuildIndexGrid(viewModel)
            : BuildList(viewModel);

        var layoutRows = new[] { GridSize.Auto, GridSize.Star() };
        return Grid([GridSize.Star()], layoutRows, toggle.Grid(row: 0), body.Grid(row: 1));
    }

    private void OnToggleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
        {
            _viewModel.OnlyIndex = toggle.IsOn;
            SettingsToolkit.WriteLocalSetting(SettingNames.IsVideoPlayerPartsOnlyIndex, toggle.IsOn);
        }
    }

    private static Element BuildList(VideoPlayerPartSectionDetailViewModel viewModel)
    {
        var rows = new List<Element>();
        foreach (var part in viewModel.Parts)
        {
            var isCurrent = viewModel.SelectedPart is not null
                && viewModel.SelectedPart.Identifier.Id == part.Identifier.Id;

            var title = TextBlock(part.Identifier.Title)
                .FontSize(13)
                .VAlign(VerticalAlignment.Center);

            var duration = TextBlock(FormatDuration(TimeSpan.FromSeconds(part.Duration)))
                .FontSize(11)
                .Opacity(0.6)
                .VAlign(VerticalAlignment.Center)
                .Grid(column: 1);

            var content = Grid(
                columns: new[] { GridSize.Star(), GridSize.Auto },
                rows: new[] { GridSize.Auto },
                title.Grid(column: 0),
                duration);

            var row = Border(content)
                .Background(ResolveBrush(isCurrent ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush"))
                .CornerRadius(4)
                .Padding(12, 8, 12, 8)
                .Margin(0, 0, 0, 8)
                .WithKey("part-" + part.Identifier.Id);

            var element = (Element)row;
            element.OnMountAdd(el => AttachClick(el, () => viewModel.SelectPartCommand.Execute(part)));
            rows.Add(element);
        }

        var stack = VStack(0, [.. rows]).Padding(12, 0, 12, 12);

        return ScrollView(stack)
            .Padding(0)
            .VerticalScrollMode(ScrollingScrollMode.Enabled);
    }

    private static Element BuildIndexGrid(VideoPlayerPartSectionDetailViewModel viewModel)
    {
        // 序号模式下用小方块铺开，等价于 XAML 里的 UniformGridLayout.
        const int columns = 8;
        var cells = new List<Element>();
        foreach (var part in viewModel.Parts)
        {
            var isCurrent = viewModel.SelectedPart is not null
                && viewModel.SelectedPart.Identifier.Id == part.Identifier.Id;

            var text = TextBlock(part.Index.ToString())
                .FontSize(14)
                .FontWeight(FontWeights.Bold)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);

            var cell = Border(text)
                .Background(ResolveBrush(isCurrent ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush"))
                .CornerRadius(4)
                .Height(40)
                .Margin(0, 0, 8, 8)
                .WithKey("idx-" + part.Index);

            var element = (Element)cell;
            element.OnMountAdd(el => AttachClick(el, () => viewModel.SelectPartCommand.Execute(part)));
            cells.Add(element);
        }

        var rows = (int)Math.Ceiling(cells.Count / (double)columns);
        var gridColumns = new GridSize[columns];
        for (var c = 0; c < columns; c++)
        {
            gridColumns[c] = GridSize.Star();
        }

        var gridRows = new GridSize[Math.Max(1, rows)];
        for (var r = 0; r < gridRows.Length; r++)
        {
            gridRows[r] = GridSize.Auto;
        }

        var grid = Grid(gridColumns, gridRows, [.. cells]).Margin(12, 0, 4, 12);

        return ScrollView(grid)
            .Padding(0)
            .VerticalScrollMode(ScrollingScrollMode.Enabled);
    }
    private static void AttachClick(FrameworkElement element, Action action)
    {
        element.Tapped += (_, args) =>
        {
            args.Handled = true;
            action();
        };
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes:D2}:{duration.Seconds:D2}";

    private static Brush ResolveBrush(string name)
    {
        if (Application.Current.Resources.TryGetValue(name, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}

/// <summary>
/// 视频信息面板.
/// 外层滚动与间距用 Reactor，UP 主信息、数据、标签、简介、操作按钮沿用原有 XAML 控件.
/// </summary>
public sealed class VideoInfoPanel : Component
{
    private readonly VideoPlayerInfoSectionDetailViewModel _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoInfoPanel"/> class.
    /// </summary>
    public VideoInfoPanel(VideoPlayerInfoSectionDetailViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        _ = UseObservableTree(_viewModel);

        var descriptor = new XamlHostElement(
            () => new VideoDescriptorControl { ViewModel = _viewModel.Page },
            element => ((VideoDescriptorControl)element).ViewModel = _viewModel.Page)
            .WithKey("info-descriptor");

        var operation = new XamlHostElement(
            () => new VideoOperationControl { ViewModel = _viewModel.Page },
            element => ((VideoOperationControl)element).ViewModel = _viewModel.Page)
            .WithKey("info-operation");

        var body = VStack(20, descriptor, operation)
            .Margin(16, 12, 16, 12);

        return ScrollView(body)
            .Padding(0)
            .HorizontalScrollMode(ScrollingScrollMode.Disabled)
            .VerticalScrollMode(ScrollingScrollMode.Enabled);
    }
}

/// <summary>
/// 视频合集（UGC 合集）面板.
/// 合集下拉与视频卡片沿用原有 XAML 控件，滚动与列表结构由 Reactor 组装.
/// </summary>
public sealed class VideoSeasonPanel : Component
{
    private readonly VideoPlayerSeasonSectionDetailViewModel _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoSeasonPanel"/> class.
    /// </summary>
    public VideoSeasonPanel(VideoPlayerSeasonSectionDetailViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var viewModel = UseObservableTree(_viewModel);

        var comboBox = new XamlHostElement(
            () => BuildSeasonComboBox(viewModel.Seasons, viewModel.SelectedSeason),
            element =>
            {
                var box = (ComboBox)element;
                box.Items.Clear();
                foreach (var season in viewModel.Seasons)
                {
                    box.Items.Add(CreateSeasonItem(season));
                }

                box.SelectedItem = box.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(p => ReferenceEquals(p.Tag, viewModel.SelectedSeason))
                    ?? box.Items.FirstOrDefault();
                box.SelectionChanged += (_, _) =>
                {
                    if (box.SelectedItem is ComboBoxItem { Tag: VideoSeason season }
                        && !ReferenceEquals(season, viewModel.SelectedSeason))
                    {
                        viewModel.ChangeSeasonCommand.Execute(season);
                    }
                };
            })
            .Margin(12, 8, 12, 0)
            .WithKey("season-combo");

        var cards = new List<Element>();
        foreach (var item in viewModel.VisibleItems)
        {
            var itemViewModel = item;
            var card = new XamlHostElement(
                () => BuildCard(itemViewModel, "PlayerSeasonVideoCardStyle"),
                element =>
                {
                    var control = (VideoCardControl)element;
                    control.Style = ResolveStyle("PlayerSeasonVideoCardStyle");
                    control.ViewModel = itemViewModel;
                })
                .Margin(0, 0, 0, 8)
                .WithKey("season-card-" + itemViewModel.Data.Identifier.Id);
            cards.Add(card);
        }

        var list = ScrollView(VStack(0, [.. cards]).Padding(12, 0, 12, 12))
            .Padding(0)
            .VerticalScrollMode(ScrollingScrollMode.Enabled);

        return Grid(
            [GridSize.Star()],
            new[] { GridSize.Auto, GridSize.Star() },
            comboBox.Grid(row: 0),
            list.Grid(row: 1));
    }

    private static ComboBox BuildSeasonComboBox(IEnumerable<VideoSeason> seasons, VideoSeason? selected)
    {
        // DisplayMemberPath 走传统 Binding，POCO 没有 ICustomProperty 会显示成空白，
        // 所以每项包一个 ComboBoxItem，文字直接写 Content，选中项靠 Tag 回查.
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var season in seasons)
        {
            box.Items.Add(CreateSeasonItem(season));
        }

        box.SelectedItem = box.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(p => ReferenceEquals(p.Tag, selected))
            ?? box.Items.FirstOrDefault();
        return box;
    }

    private static ComboBoxItem CreateSeasonItem(VideoSeason season)
        => new() { Content = season.Title, Tag = season };

    private static VideoCardControl BuildCard(VideoItemViewModel item, string styleKey)
        => new() { Style = ResolveStyle(styleKey), ViewModel = item };

    private static Style ResolveStyle(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Style style ? style : null!;
}

/// <summary>
/// 播放列表（合订集）面板.
/// 卡片沿用原有 XAML 控件，列表结构由 Reactor 组装.
/// </summary>
public sealed class VideoPlaylistPanel : Component
{
    private readonly VideoPlayerPlaylistSectionDetailViewModel _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoPlaylistPanel"/> class.
    /// </summary>
    public VideoPlaylistPanel(VideoPlayerPlaylistSectionDetailViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var viewModel = UseObservableTree(_viewModel);

        var cards = new List<Element>();
        foreach (var item in viewModel.Items)
        {
            var itemViewModel = item;
            var card = new XamlHostElement(
                () => new VideoCardControl { Style = ResolveStyle("PlayerPlaylistVideoCardStyle"), ViewModel = itemViewModel },
                element =>
                {
                    var control = (VideoCardControl)element;
                    control.Style = ResolveStyle("PlayerPlaylistVideoCardStyle");
                    control.ViewModel = itemViewModel;
                })
                .Margin(0, 0, 0, 8)
                .WithKey("playlist-card-" + itemViewModel.Data.Identifier.Id);
            cards.Add(card);
        }

        return ScrollView(VStack(0, [.. cards]).Padding(12))
            .Padding(0)
            .VerticalScrollMode(ScrollingScrollMode.Enabled);
    }

    private static Style ResolveStyle(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Style style ? style : null!;
}

