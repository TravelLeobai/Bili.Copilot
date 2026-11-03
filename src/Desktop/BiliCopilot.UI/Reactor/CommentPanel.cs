// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Specialized;
using System.Windows.Input;
using BiliCopilot.UI.Controls.Comment;
using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Richasy.BiliKernel.Models;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 评论面板.
/// 用 Reactor 重写评论列表与评论卡片，输入框沿用原有控件.
/// </summary>
public sealed class CommentPanel : Component
{
    private const double LoadMoreThreshold = 400d;

    private readonly CommentMainViewModel _viewModel;
    private int _version;
    private ScrollView? _scrollView;
    private ScrollView? _detailScrollView;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentPanel"/> class.
    /// </summary>
    public CommentPanel(CommentMainViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        var viewModel = UseObservableTree(_viewModel);
        var (version, setVersion) = UseState(0);
        _ = version;

        UseEffect(
            () =>
            {
                void OnChanged(object? sender, EventArgs e) => setVersion(++_version);
                void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => setVersion(++_version);
                viewModel.ListUpdated += OnChanged;
                viewModel.Comments.CollectionChanged += OnCollectionChanged;
                return () =>
                {
                    viewModel.ListUpdated -= OnChanged;
                    viewModel.Comments.CollectionChanged -= OnCollectionChanged;
                };
            },
            Array.Empty<object>());

        UseEffect(
            () =>
            {
                var detail = _viewModel.SelectedItem;
                if (detail is null)
                {
                    return () => { };
                }

                void OnChanged(object? sender, EventArgs e) => setVersion(++_version);
                void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => setVersion(++_version);
                detail.ListUpdated += OnChanged;
                detail.Comments.CollectionChanged += OnCollectionChanged;
                return () =>
                {
                    detail.ListUpdated -= OnChanged;
                    detail.Comments.CollectionChanged -= OnCollectionChanged;
                };
            },
            viewModel.SelectedItem);

        if (viewModel.SelectedItem is { } detail)
        {
            var detailCards = new List<Element>
            {
                CreateCard(detail.Data, null, false, "detail-main", () => setVersion(++_version)),
            };

            foreach (var reply in detail.Comments)
            {
                detailCards.Add(CreateCard(reply, null, false, "reply-" + reply.Data.Id, () => setVersion(++_version)));
            }

            var detailList = ScrollView(
                VStack(0, detailCards.ToArray()).Padding(12, 4, 12, 12))
                .Padding(0)
                .VerticalScrollMode(ScrollingScrollMode.Enabled)
                .OnMountAdd(element =>
                {
                    if (element is ScrollView scroll)
                    {
                        _detailScrollView = scroll;
                        scroll.ViewChanged += OnDetailScrollChanged;
                    }
                })
                .OnUnmountAdd(element =>
                {
                    if (element is ScrollView scroll)
                    {
                        scroll.ViewChanged -= OnDetailScrollChanged;
                        if (ReferenceEquals(_detailScrollView, scroll))
                        {
                            _detailScrollView = null;
                        }
                    }
                });

            var detailStatus = detail.IsLoading
                ? (Element)TextBlock("正在加载…").FontSize(12).Opacity(0.6).HAlign(HorizontalAlignment.Center)
                : detail.IsEmpty
                    ? TextBlock("还没有回复").FontSize(13).Opacity(0.6).HAlign(HorizontalAlignment.Center)
                    : TextBlock(string.Empty);

            var detailInput = new XamlHostElement(
                () => new CommentTextBox(),
                element =>
                {
                    var box = (CommentTextBox)element;
                    box.Margin = new Thickness(16, 0, 16, 0);
                    box.SendCommand = detail.SendReplyCommand;
                    box.ResetSelectedCommand = detail.ResetReplyTargetCommand;
                    box.ReplyTip = detail.ReplyTarget;
                    box.IsEnabled = !detail.IsReplying;
                });

            return Grid(
                columns: new[] { GridSize.Star() },
                rows: new[] { GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto },
                HyperlinkButton("返回上一级", onClick: () => viewModel.BackToMainCommand.Execute(default))
                    .Margin(16, 2, 0, 2)
                    .Grid(row: 0, column: 0),
                detailStatus.Grid(row: 1, column: 0),
                detailList.Grid(row: 2, column: 0),
                detailInput.Grid(row: 3, column: 0))
                .Padding(0, 6, 0, 12);
        }

        var sorts = viewModel.Sorts ?? new List<CommentSortType>();
        var sortNames = sorts.Select(GetSortName).ToArray();
        var selectedIndex = Math.Max(sorts.IndexOf(viewModel.SortType), 0);

        var header = Grid(
            columns: new[] { GridSize.Star(), GridSize.Auto },
            rows: new[] { GridSize.Auto },
            TextBlock(viewModel.Title)
                .FontSize(16)
                .SemiBold()
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),
            ComboBox(
                sortNames,
                selectedIndex,
                index =>
                {
                    if (index >= 0 && index < sorts.Count)
                    {
                        viewModel.ChangeSortTypeCommand.Execute(sorts[index]);
                    }
                })
                .MinWidth(100)
                .Margin(8, 0, 0, 0)
                .Grid(row: 0, column: 1))
            .Padding(16, 2, 16, 2);

        void Refresh() => setVersion(++_version);

        var cards = new List<Element>();
        if (viewModel.TopItem is not null)
        {
            cards.Add(CreateCard(viewModel.TopItem.Data, viewModel.TopItem.ShowMoreCommand, true, "top", Refresh));
        }

        foreach (var comment in viewModel.Comments)
        {
            cards.Add(CreateCard(comment.Data, comment.ShowMoreCommand, true, comment.Data.Data.Id, Refresh));
        }

        var list = ScrollView(
            VStack(0, cards.ToArray()).Padding(12, 4, 12, 12))
            .Padding(0)
            .VerticalScrollMode(ScrollingScrollMode.Enabled)
            .OnMountAdd(element =>
            {
                if (element is ScrollView scroll)
                {
                    _scrollView = scroll;
                    scroll.ViewChanged += OnScrollChanged;
                }
            })
            .OnUnmountAdd(element =>
            {
                if (element is ScrollView scroll)
                {
                    scroll.ViewChanged -= OnScrollChanged;
                    if (ReferenceEquals(_scrollView, scroll))
                    {
                        _scrollView = null;
                    }
                }
            });

        Element status = viewModel.IsLoading
            ? TextBlock("正在加载…").FontSize(12).Opacity(0.6).Margin(0, 8, 0, 0).HAlign(HorizontalAlignment.Center)
            : viewModel.IsEmpty
                ? TextBlock("还没有评论").FontSize(13).Opacity(0.6).Margin(0, 24, 0, 0).HAlign(HorizontalAlignment.Center)
                : TextBlock(string.Empty);

        var input = new XamlHostElement(
            () => new CommentTextBox(),
            element =>
            {
                var box = (CommentTextBox)element;
                box.Margin = new Thickness(16, 0, 16, 0);
                box.SendCommand = viewModel.SendReplyCommand;
                box.ResetSelectedCommand = viewModel.ResetReplyTargetCommand;
                box.ReplyTip = viewModel.ReplyTarget;
                box.IsEnabled = !viewModel.IsReplying;
            });

        return Grid(
            columns: new[] { GridSize.Star() },
            rows: new[] { GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto },
            header.Grid(row: 0, column: 0),
            status.Grid(row: 1, column: 0),
            list.Grid(row: 2, column: 0),
            input.Grid(row: 3, column: 0))
            .Padding(0, 12, 0, 12);
    }

    private static string GetSortName(CommentSortType sortType)
        => sortType == CommentSortType.Time ? "最新评论" : "最热评论";

    private void OnDetailScrollChanged(ScrollView sender, object args)
    {
        if (sender.ScrollableHeight - sender.VerticalOffset < LoadMoreThreshold)
        {
            _viewModel.SelectedItem?.LoadItemsCommand.Execute(default);
        }
    }

    private void OnScrollChanged(ScrollView sender, object args)
    {
        if (sender.ScrollableHeight - sender.VerticalOffset < LoadMoreThreshold)
        {
            _viewModel.LoadItemsCommand.Execute(default);
        }
    }

    private static Element CreateCard(CommentItemViewModel comment, ICommand? showMore, bool isMoreEnabled, string key, Action refresh)
    {
        var avatar = Button(
            Border(
                Image(comment.Avatar?.ToString() ?? string.Empty)
                    .Size(40, 40)
                    .AccessibilityHidden())
                .CornerRadius(20),
            () => comment.ShowUserSpaceCommand.Execute(default))
            .AutomationName(comment.UserName)
            .SubtleButton()
            .Padding(0);

        Element topBadge = comment.IsTop
            ? Border(
                TextBlock("置顶")
                    .FontSize(10)
                    .Foreground(ResolveBrush("AccentTextFillColorPrimaryBrush", Microsoft.UI.Colors.SkyBlue)))
                .CornerRadius(4)
                .Padding(6, 0, 6, 0)
                .Margin(0, 8, 0, 0)
                .WithBorder(ThemeBrush("AccentFillColorDefaultBrush"), 1)
            : (Element)TextBlock(string.Empty);

        var nameRow = Grid(
            columns: new[] { GridSize.Star(), GridSize.Auto },
            rows: new[] { GridSize.Auto },
            TextBlock(comment.UserName)
                .FontSize(14)
                .SemiBold()
                .Grid(row: 0, column: 0),
            TextBlock(comment.RelativeTime)
                .FontSize(11)
                .Opacity(0.6)
                .Margin(8, 0, 0, 0)
                .Grid(row: 0, column: 1));

        Element content = TextBlock(comment.Content?.Text ?? string.Empty)
            .FontSize(13)
            .TextWrapping(TextWrapping.Wrap);

        var likeButton = new XamlHostElement(
            () => new CommentLikeButton { VerticalAlignment = VerticalAlignment.Center },
            element => ((CommentLikeButton)element).ViewModel = comment)
            .Height(32);

        Element moreLink = isMoreEnabled && comment.ChildCount > 0 && showMore is not null
            ? HyperlinkButton(
                $"{comment.ChildCount} 条回复",
                onClick: () => showMore.Execute(default))
            : (Element)TextBlock(string.Empty);

        var replyButton = new XamlHostElement(
            () => CreateReplyButton(),
            element => ((Button)element).Command = comment.MarkReplyTargetCommand)
            .Height(32);

        var actionRow = Grid(
            columns: new[] { GridSize.Auto, GridSize.Star(), GridSize.Auto },
            rows: new[] { GridSize.Auto },
            likeButton.Grid(row: 0, column: 0).VAlign(VerticalAlignment.Center),
            moreLink.Grid(row: 0, column: 1).HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center).Margin(12, 0, 0, 0),
            replyButton.Grid(row: 0, column: 2).HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Center))
            .Margin(0, 8, 0, 0);

        var body = Grid(
            columns: new[] { GridSize.Star() },
            rows: new[] { GridSize.Auto, GridSize.Auto, GridSize.Auto },
            nameRow.Grid(row: 0, column: 0),
            content.Grid(row: 1, column: 0).Margin(0, 6, 0, 0),
            actionRow.Grid(row: 2, column: 0));

        return Border(
            Grid(
                columns: new[] { GridSize.Auto, GridSize.Star() },
                rows: new[] { GridSize.Auto },
                VStack(0, avatar, topBadge)
                    .Grid(row: 0, column: 0)
                    .VAlign(VerticalAlignment.Top)
                    .Margin(0, 0, 12, 0),
                body.Grid(row: 0, column: 1)))
            .Background(ThemeBrush("CardBackgroundFillColorDefaultBrush"))
            .CornerRadius(4)
            .Padding(12)
            .Margin(0, 0, 0, 8)
            .WithKey(key);
    }

    private static Button CreateReplyButton()
    {
        var button = new Button
        {
            Style = Application.Current.Resources.TryGetValue("SmallIconButtonStyle", out var style) && style is Style buttonStyle ? buttonStyle : null,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FluentIcons.WinUI.SymbolIcon
            {
                Symbol = FluentIcons.Common.Symbol.CommentMention,
                FontSize = 12,
            },
        };
        ToolTipService.SetToolTip(button, "回复");
        return button;
    }

    private static Brush ThemeBrush(string name)
        => ResolveBrush(name, Microsoft.UI.Colors.Transparent);

    private static Brush ResolveBrush(string name, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(name, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
