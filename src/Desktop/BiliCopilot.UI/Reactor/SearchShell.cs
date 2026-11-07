// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.Controls.Search;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Richasy.WinUIKernel.Share.Toolkits;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 搜索结果页外壳（Reactor 重写）.
/// 只重排顶栏与分区栏：搜索框复用搜索首页的 XAML 控件，排序按钮与视频卡片仍是原有 XAML 控件，
/// 分区栏用 SelectorBar 横向排在顶栏下面（下划线选中样式）.
/// </summary>
public sealed class SearchShell : Component
{
    private readonly SearchPageViewModel _viewModel;
    private readonly AppSearchBox _searchBox = new();
    private readonly VideoSearchExtraHeader _videoExtra = new();
    private readonly Dictionary<SearchSectionType, FrameworkElement> _contentCache = [];

    private string _appliedKeyword = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchShell"/> class.
    /// </summary>
    /// <param name="viewModel">搜索页视图模型.</param>
    public SearchShell(SearchPageViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var viewModel = UseObservableTree(_viewModel);
        var sections = viewModel.Sections ?? [];
        var selectedIndex = Math.Max(0, sections.IndexOf(viewModel.SelectedSection));

        var title = TextBlock($"搜索 {viewModel.Keyword}")
            .FontSize(16)
            .SemiBold()
            .VAlign(VerticalAlignment.Center);

        var searchBoxHost = ViewLaterShell.Host(_searchBox, _ => ApplySearchBox(viewModel))
            .Width(320)
            .VAlign(VerticalAlignment.Center);

        // 排序按钮仍是原有 XAML 控件，只在视频分区显示.
        Element extra = viewModel.SelectedSection is VideoSearchSectionDetailViewModel videoSection
            ? ViewLaterShell.Host(_videoExtra, control => control.ViewModel = videoSection)
                .VAlign(VerticalAlignment.Center)
            : TextBlock(string.Empty);

        var refresh = BuildRefreshButton(viewModel);

        var header = Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto],
            [GridSize.Star()],
            title.Grid(row: 0, column: 0).Margin(0, 0, 16, 0),
            searchBoxHost.Grid(row: 0, column: 1).HAlign(HorizontalAlignment.Right),
            extra.Grid(row: 0, column: 2).Margin(8, 0, 0, 0),
            refresh.Grid(row: 0, column: 3).Margin(8, 0, 0, 0))
            .Padding(16, 0, 16, 0)
            .WithKey("search-header");

        var tabs = BuildSectionTabs(sections, selectedIndex, viewModel);

        return Grid(
            [GridSize.Star()],
            [GridSize.Px(56), GridSize.Auto, GridSize.Px(1), GridSize.Star()],
            header.Grid(row: 0, column: 0),
            tabs.Grid(row: 1, column: 0),
            ViewLaterShell.Divider().Grid(row: 2, column: 0),
            BuildContent(viewModel).Grid(row: 3, column: 0))
            .WithKey("search-shell");
    }

    /// <summary>
    /// 分区栏：SelectorBar（选中项带下划线），横向排在顶栏下面.
    /// </summary>
    private Element BuildSectionTabs(
        IReadOnlyList<ISearchSectionDetailViewModel> sections,
        int selectedIndex,
        SearchPageViewModel viewModel)
    {
        if (sections.Count == 0)
        {
            return TextBlock(string.Empty);
        }

        var items = sections
            .Select(section => new SelectorBarItemData(GetSectionName(section.SectionType)))
            .ToArray();

        return SelectorBar(
                items,
                Optional<int>.Of(selectedIndex),
                index =>
                {
                    if (index >= 0 && index < sections.Count)
                    {
                        viewModel.SelectSectionCommand.Execute(sections[index]);
                    }
                })
            .Padding(16, 0, 16, 0)
            .WithKey("search-tabs");
    }

    private Element BuildRefreshButton(SearchPageViewModel viewModel)
        => new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new FluentIcons.WinUI.SymbolIcon
                            {
                                Symbol = FluentIcons.Common.Symbol.ArrowSync,
                                FontSize = 12,
                            },
                            new TextBlock { Text = ResourceToolkit.GetLocalizedString(StringNames.Refresh) },
                        },
                    },
                    VerticalAlignment = VerticalAlignment.Center,
                };
                return button;
            },
            element =>
            {
                var button = (Button)element;
                var section = viewModel.SelectedSection;
                button.Command = section?.RefreshCommand;
                button.IsEnabled = section is not null && !section.IsEmpty;
            })
            .VAlign(VerticalAlignment.Center)
            .WithKey("search-refresh");

    /// <summary>
    /// 关键词变化时把当前搜索词预填进顶栏搜索框；用户点一下就会清空，直接开新搜索.
    /// </summary>
    private void ApplySearchBox(SearchPageViewModel viewModel)
    {
        var keyword = viewModel.Keyword ?? string.Empty;
        if (string.Equals(keyword, _appliedKeyword, StringComparison.Ordinal))
        {
            return;
        }

        _appliedKeyword = keyword;

        // 结果页这个搜索框不该在页面加载时抢焦点（会连带弹出建议浮层），
        // 从 Tab 序列里摘掉，用户点击时仍可正常输入.
        _searchBox.IsTabStop = false;
        _searchBox.PrefillKeyword(keyword);
    }

    private Element BuildContent(SearchPageViewModel viewModel)
    {
        if (viewModel.SelectedSection is not { } section)
        {
            return TextBlock(string.Empty);
        }

        var type = section.SectionType;
        if (!_contentCache.TryGetValue(type, out var control))
        {
            control = CreateSectionControl(section);
            _contentCache[type] = control;
        }

        return ViewLaterShell.Host(control, element => ApplySectionViewModel(element, section), viewModel);
    }

    private static FrameworkElement CreateSectionControl(ISearchSectionDetailViewModel section)
        => section switch
        {
            VideoSearchSectionDetailViewModel => new VideoSectionDetailControl(),
            PgcSearchSectionDetailViewModel => new PgcSectionDetailControl(),
            LiveSearchSectionDetailViewModel => new LiveSectionDetailControl(),
            UserSearchSectionDetailViewModel => new UserSectionDetailControl(),
            ArticleSearchSectionDetailViewModel => new ArticleSectionDetailControl(),
            _ => new Grid(),
        };

    private static void ApplySectionViewModel(FrameworkElement element, ISearchSectionDetailViewModel section)
    {
        switch (element)
        {
            case VideoSectionDetailControl control:
                control.ViewModel = (VideoSearchSectionDetailViewModel)section;
                break;
            case PgcSectionDetailControl control:
                control.ViewModel = (PgcSearchSectionDetailViewModel)section;
                break;
            case LiveSectionDetailControl control:
                control.ViewModel = (LiveSearchSectionDetailViewModel)section;
                break;
            case UserSectionDetailControl control:
                control.ViewModel = (UserSearchSectionDetailViewModel)section;
                break;
            case ArticleSectionDetailControl control:
                control.ViewModel = (ArticleSearchSectionDetailViewModel)section;
                break;
        }
    }

    private static string GetSectionName(SearchSectionType type)
        => type switch
        {
            SearchSectionType.Video => ResourceToolkit.GetLocalizedString(StringNames.Video),
            SearchSectionType.Anime => ResourceToolkit.GetLocalizedString(StringNames.Anime),
            SearchSectionType.Live => ResourceToolkit.GetLocalizedString(StringNames.Live),
            SearchSectionType.Cinema => ResourceToolkit.GetLocalizedString(StringNames.Cinema),
            SearchSectionType.User => ResourceToolkit.GetLocalizedString(StringNames.User),
            SearchSectionType.Article => ResourceToolkit.GetLocalizedString(StringNames.Article),
            _ => type.ToString(),
        };
}

