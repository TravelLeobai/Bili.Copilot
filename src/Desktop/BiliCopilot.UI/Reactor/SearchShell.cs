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
using Microsoft.UI.Xaml.Media;
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
    /// <summary>
    /// 顶栏与分区栏的宽度分界（DIP）：宽于此值排序/刷新留在顶栏，窄于此值挪到分区栏那一行.
    /// </summary>
    private const double WideBreakpointDip = 1000d;

    private readonly SearchPageViewModel _viewModel;
    private readonly AppSearchBox _searchBox = new();
    private readonly VideoSearchExtraHeader _videoExtra = new();
    private readonly Dictionary<SearchSectionType, FrameworkElement> _contentCache = [];

    private string _appliedKeyword = string.Empty;
    private bool _popupCleanupDone;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchShell"/> class.
    /// </summary>
    /// <param name="viewModel">搜索页视图模型.</param>
    public SearchShell(SearchPageViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var viewModel = UseObservableTree(_viewModel);
        CloseLingeringSuggestions();
        var sections = viewModel.Sections ?? [];
        var selectedIndex = Math.Max(0, sections.IndexOf(viewModel.SelectedSection));

        // 窗口够宽时排序/刷新留在顶栏；窄窗口顶栏只放搜索框，
        // 排序和刷新挪到分区栏那一行，避免三个东西挤在一起互相压住.
        var isWide = UseBreakpoint(WideBreakpointDip);

        var searchBoxHost = ViewLaterShell.Host(_searchBox, _ => ApplySearchBox(viewModel))
            .VAlign(VerticalAlignment.Center);
        if (isWide)
        {
            searchBoxHost = searchBoxHost.Width(560);
        }

        // 排序按钮仍是原有 XAML 控件，只在视频分区显示.
        Element extra = viewModel.SelectedSection is VideoSearchSectionDetailViewModel videoSection
            ? ViewLaterShell.Host(_videoExtra, control => control.ViewModel = videoSection)
                .VAlign(VerticalAlignment.Center)
            : TextBlock(string.Empty);

        var refresh = BuildRefreshButton(viewModel);

        var actions = Grid(
            [GridSize.Auto, GridSize.Auto],
            [GridSize.Star()],
            extra.Grid(row: 0, column: 0),
            refresh.Grid(row: 0, column: 1).Margin(8, 0, 0, 0))
            .VAlign(VerticalAlignment.Center);

        Element header;
        Element tabs;
        if (isWide)
        {
            // 顶栏：搜索框居中，排序/刷新靠右.
            header = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                searchBoxHost.Grid(row: 0, column: 0).HAlign(HorizontalAlignment.Center),
                actions.Grid(row: 0, column: 0).HAlign(HorizontalAlignment.Right))
                .Padding(16, 0, 16, 0)
                .WithKey("search-header");

            // 分区栏单独一行，右侧不放东西.
            tabs = BuildSectionTabs(sections, selectedIndex, viewModel).WithKey("search-tabs-row");
        }
        else
        {
            // 窄窗口：顶栏只放搜索框，铺满整行.
            header = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                searchBoxHost.Grid(row: 0, column: 0))
                .Padding(16, 0, 16, 0)
                .WithKey("search-header-narrow");

            // 分区栏这一行右侧接上排序和刷新.
            tabs = Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                BuildSectionTabs(sections, selectedIndex, viewModel).Grid(row: 0, column: 0),
                actions.Grid(row: 0, column: 1).Margin(0, 0, 16, 0))
                .WithKey("search-tabs-row-narrow");
        }

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

        // 不传 selectedIndex：传了它就是受控模式，用户点击会被立刻改回原值、回调等于不生效
        // （之前切分类没反应就是这个原因）。用 key 带上当前索引，程序化切换时重建一次即可.
        return SelectorBar(
                items,
                Optional<int>.Unset,
                index =>
                {
                    if (index >= 0 && index < sections.Count)
                    {
                        viewModel.SelectSectionCommand.Execute(sections[index]);
                    }
                })
            .Padding(16, 0, 16, 0)
            .WithKey($"search-tabs-{viewModel.Keyword}");
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
                // 分区结果为空时也允许刷新（之前按 IsEmpty 禁用，空分区就成了死按钮）.
                button.IsEnabled = section is not null;
            })
            .VAlign(VerticalAlignment.Center)
            .WithKey("search-refresh");

    /// <summary>
    /// 搜索首页那个搜索框的建议浮层是 Popup，跳到结果页后仍浮在最上层，会挡住分区栏的点击.
    /// 结果页首次渲染时把整棵树里所有 AutoSuggestBox 的建议列表关掉.
    /// </summary>
    private void CloseLingeringSuggestions()
    {
        if (_popupCleanupDone)
        {
            return;
        }

        _popupCleanupDone = true;
        _searchBox.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (_searchBox.XamlRoot?.Content is not DependencyObject root)
                {
                    return;
                }

                foreach (var box in FindSuggestBoxes(root))
                {
                    box.IsSuggestionListOpen = false;
                }
            }
            catch
            {
                // 关不掉也不影响其它功能.
            }
        });
    }

    private static IEnumerable<AutoSuggestBox> FindSuggestBoxes(DependencyObject node)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is AutoSuggestBox box)
            {
                yield return box;
            }

            foreach (var nested in FindSuggestBoxes(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// 关键词变化时把当前搜索词预填进顶栏搜索框；用户点一下就会清空，直接开新搜索.
    /// </summary>
    private void ApplySearchBox(SearchPageViewModel viewModel)
    {
        // 每次渲染都压一次建议浮层：它会在各种时机被重新打开，只压一次压不住.
        _searchBox.SuppressSuggestions();

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

        // key 带上分区类型：换分区时必须把旧的内容控件换下去，
        // 否则旧控件的卡片还挂在树上，看起来就是"切了没变化".
        return ViewLaterShell.Host(control, element => ApplySectionViewModel(element, section), viewModel)
            .WithKey($"search-content-{type}");
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









