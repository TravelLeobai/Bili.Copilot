// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Specialized;
using System.Windows.Input;
using BiliCopilot.UI.Controls.Comment;
using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Richasy.BiliKernel.Models;
using Richasy.WinUIKernel.Share.Toolkits;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 评论面板.
/// 用 Reactor 重写评论列表与评论卡片，输入框沿用原有控件.
/// </summary>
public sealed class CommentPanel : Component
{

    // 和 XAML 版评论卡片保持一致（ControlCornerRadius = 8）
    private const double ControlCornerRadiusValue = 8d;

    private readonly CommentMainViewModel _viewModel;
    private int _version;

    private int _renderCount;
    private int _totalCount;
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
                // 表情包（含装扮表情）首次渲染时拉一次，拿到后重绘评论正文.
                _ = LoadEmotesAsync(() => setVersion(++_version));
            },
            Array.Empty<object>());

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
            // 回复列表同样走 VirtualList：第一条是主评论，后面是回复
            var replyItems = new List<CommentItemViewModel> { detail.Data };
            foreach (var reply in detail.Comments)
            {
                replyItems.Add(reply);
            }

            var detailList = VirtualList(
                replyItems.Count,
                index =>
                {
                    var item = replyItems[index];
                    var key = index == 0 ? "detail-main" : "reply-" + item.Data.Id;
                    return CreateCard(item, null, false, key, () => setVersion(++_version)).Margin(12, 4, 12, 4);
                },
                index => index == 0 ? "detail-main" : replyItems[index].Data.Id.ToString(),
                estimatedItemHeight: 132,
                spacing: 0,
                onVisibleRangeChanged: (_, last) =>
                {
                    // 快到底部时取下一页回复
                    if (last >= replyItems.Count - 3)
                    {
                        detail.LoadItemsCommand.Execute(default);
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

        // 置顶 + 普通评论拼成一个序列，交给 VirtualList 做真虚拟化（滚出屏幕的卡片会被回收）
        var listItems = new List<(CommentItemViewModel Data, ICommand? ShowMore)>();
        if (viewModel.TopItem is not null)
        {
            listItems.Add((viewModel.TopItem.Data, viewModel.TopItem.ShowMoreCommand));
        }

        foreach (var comment in viewModel.Comments)
        {
            listItems.Add((comment.Data, comment.ShowMoreCommand));
        }

        _renderCount = 0;
        _totalCount = listItems.Count;

        var list = VirtualList(
            listItems.Count,
            index =>
            {
                _renderCount++;
                var item = listItems[index];
                return CreateCard(item.Data, item.ShowMore, true, "c-" + item.Data.Data.Id, Refresh).Margin(12, 4, 12, 4);
            },
            index => listItems[index].Data.Data.Id.ToString(),
            estimatedItemHeight: 132,
            spacing: 0,
            onVisibleRangeChanged: (first, last) =>
            {
                Serilog.Log.Information(
                    "评论虚拟化：可见 {First}-{Last}，已渲染 {Rendered} 项，总数 {Total}",
                    first,
                    last,
                    _renderCount,
                    _totalCount);

                // 快到底部时取下一页
                if (last >= listItems.Count - 3)
                {
                    viewModel.LoadItemsCommand.Execute(default);
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
                .CornerRadius(ControlCornerRadiusValue)
                .Padding(2)
                .Margin(0, 12, 0, 0)
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

        Element content = BuildCommentContent(comment.Content?.Text ?? string.Empty);

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
    /// <summary>
    /// 拉一次表情面板；拿到之后触发重绘，把 [名字] 换成图.
    /// </summary>
    private static async Task LoadEmotesAsync(Action refresh)
    {
        if (BiliEmoteService.IsReady)
        {
            return;
        }

        try
        {
            var cookie = GlobalDependencies.Kernel
                .Get<Richasy.BiliKernel.Bili.Authorization.IBiliCookiesResolver>()
                ?.GetCookieString() ?? string.Empty;
            var logger = GlobalDependencies.Kernel.Get<Microsoft.Extensions.Logging.ILogger<CommentPanel>>();
            await BiliEmoteService.EnsureLoadedAsync(cookie, logger).ConfigureAwait(false);
            refresh();
        }
        catch (Exception ex)
        {
            // 表情拉不到不影响评论正文显示.
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                GlobalDependencies.Kernel.Get<Microsoft.Extensions.Logging.ILogger<CommentPanel>>(),
                ex,
                "评论表情加载失败");
        }
    }

    /// <summary>
    /// 评论正文：有表情占位就渲染富文本，否则用普通 TextBlock.
    /// </summary>
    private static Element BuildCommentContent(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('['))
        {
            return TextBlock(text).FontSize(13).TextWrapping(TextWrapping.Wrap);
        }

        return new XamlHostElement(
            () => CreateRichText(text),
            element =>
            {
                var block = (RichTextBlock)element;
                block.Blocks.Clear();
                block.Blocks.Add(BuildParagraph(text));
            });
    }

    private static RichTextBlock CreateRichText(string text)
    {
        var block = new RichTextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        block.Blocks.Add(BuildParagraph(text));
        return block;
    }

    private static Paragraph BuildParagraph(string text)
    {
        var paragraph = new Paragraph();
        foreach (var (chunk, emote) in SplitEmotes(text))
        {
            if (emote is null)
            {
                paragraph.Inlines.Add(new Run { Text = chunk });
                continue;
            }

            var size = emote.Size >= 2 ? 32d : 22d;
            var image = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(emote.Url)),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(image, emote.Token);
            paragraph.Inlines.Add(new InlineUIContainer { Child = image });
        }

        return paragraph;
    }

    /// <summary>
    /// 把正文按 [名字] 切成文本段和表情段.
    /// </summary>
    private static IEnumerable<(string Chunk, BiliEmote? Emote)> SplitEmotes(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var open = text.IndexOf('[', start);
            var close = open < 0 ? -1 : text.IndexOf(']', open + 1);
            if (open < 0 || close < 0 || close - open > 32)
            {
                yield return (text[start..], null);
                yield break;
            }

            if (open > start)
            {
                yield return (text[start..open], null);
            }

            var token = text[open..(close + 1)];
            var emote = BiliEmoteService.TryGet(token);
            if (emote is null)
            {
                yield return (token, null);
            }
            else
            {
                yield return (token, emote);
            }

            start = close + 1;
        }
    }
}
