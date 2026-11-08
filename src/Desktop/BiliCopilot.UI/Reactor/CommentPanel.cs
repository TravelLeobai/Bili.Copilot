// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Specialized;
using System.Windows.Input;
using BiliCopilot.UI.Controls.Comment;
using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Items;
using BiliCopilot.UI.Extensions;
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
    private int _lastReplyVersion;

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
                    var key = index == 0 ? "detail-main" : $"reply-{index}-{item.Data.Id}";
                    return CreateCard(item, null, false, key, () => setVersion(++_version)).Margin(12, 4, 12, 4);
                },
                index => index == 0 ? "detail-main" : $"reply-{index}-{replyItems[index].Data.Id}",
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
                    if (viewModel.ShouldFocusInput)
                    {
                        viewModel.ShouldFocusInput = false;
                        box.Focus(FocusState.Programmatic);
                    }
                });

            // 返回按钮改成箭头图标：文字版在窄面板里会挤掉其它元素.
            var backButton = CreateBackButton(viewModel);

            return Grid(
                columns: new[] { GridSize.Star() },
                rows: new[] { GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto },
                // 左边距 12 = 评论卡片的 Margin(12,4,12,4)，让箭头左缘与卡片左缘齐平；顶部再加一点留白.
                backButton.Grid(row: 0, column: 0).Margin(12, 12, 0, 8).HAlign(HorizontalAlignment.Left),
                detailStatus.Grid(row: 1, column: 0),
                detailList.WithKey("detail-list").Grid(row: 2, column: 0),
                detailInput.WithKey("detail-input").Grid(row: 3, column: 0))
                .Padding(0, 6, 0, 12)
                .WithKey("panel-detail");
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

        var list = VirtualList(
            listItems.Count,
            index =>
            {
                var item = listItems[index];

                // key 里带上位置：置顶评论通常同时出现在 Comments 里，只按 rpid 做 key 会撞车，
                // Reactor 会把同一个元素复用给两处，第一张卡片的按钮就失效了.
                // 第一张卡片的上边距 12：和顶栏上方"SelectorBar → 评论列表"那 14 的间距对齐
                // （面板顶 padding 12 + 顶栏自身 2 = 14；这边是顶栏底 padding 2 + 卡片 12）.
                return CreateCard(item.Data, item.ShowMore, true, $"c-{index}-{item.Data.Data.Id}", Refresh)
                    .Margin(12, index == 0 ? 14 : 4, 12, 4);
            },
            index => $"c-{index}-{listItems[index].Data.Data.Id}",
            estimatedItemHeight: 132,
            spacing: 0,
            onVisibleRangeChanged: (first, last) =>
            {
                // 快到底部时取下一页。IsLoading 守卫是必须的：
                // 数据回来之前每次布局都会再触发一遍，会连续发请求、把 UI 线程刷满.
                if (!viewModel.IsLoading && last >= listItems.Count - 3)
                {
                    viewModel.LoadItemsCommand.Execute(default);
                }
            });


        // 空闲时别留一个空 TextBlock：它仍占一行行高，会把第一张卡片和顶栏的间距撑大一截.
        Element status = viewModel.IsLoading
            ? TextBlock("正在加载…").FontSize(12).Opacity(0.6).Margin(0, 8, 0, 0).HAlign(HorizontalAlignment.Center)
            : viewModel.IsEmpty
                ? TextBlock("还没有评论").FontSize(13).Opacity(0.6).Margin(0, 24, 0, 0).HAlign(HorizontalAlignment.Center)
                : TextBlock(string.Empty).Height(0);

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

                // 每次点回复按钮（版本号 +1）就把「@某人 」直接写进输入框并抢焦点：
                // 只看输入框上方的小字提示太不明显，用户会以为按钮没反应.
                if (viewModel.ReplyTargetVersion != _lastReplyVersion)
                {
                    _lastReplyVersion = viewModel.ReplyTargetVersion;
                    if (!string.IsNullOrEmpty(viewModel.ReplyTarget))
                    {
                        box.Text = $"@{viewModel.ReplyTarget} ";
                        box.Focus(FocusState.Programmatic);
                    }
                }

                if (viewModel.ShouldFocusInput)
                {
                    viewModel.ShouldFocusInput = false;
                    box.Focus(FocusState.Programmatic);
                }
            });

        return Grid(
            columns: new[] { GridSize.Star() },
            rows: new[] { GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto },
            header.WithKey("panel-head").Grid(row: 0, column: 0),
            status.Grid(row: 1, column: 0),
            list.WithKey("main-list").Grid(row: 2, column: 0),
            input.WithKey("main-input").Grid(row: 3, column: 0))
            .Padding(0, 12, 0, 12)
            .WithKey("panel-main");
    }

    private static string GetSortName(CommentSortType sortType)
        => sortType == CommentSortType.Time ? "最新评论" : "最热评论";

    private static Element CreateCard(CommentItemViewModel comment, ICommand? showMore, bool isMoreEnabled, string key, Action refresh)
    {
        // 40 口径头像按 80 像素解码，同一个地址走缓存不重复解码.
        var avatarUrl = comment.Avatar?.ToString();
        var avatar = Button(
            Border(
                Image(avatarUrl ?? string.Empty)
                    .Set(image => image.Source = SmallImageCache.Get(avatarUrl, 40) ?? image.Source)
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
                    .TextAlignment(TextAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                    .Foreground(ResolveBrush("AccentTextFillColorPrimaryBrush", Microsoft.UI.Colors.SkyBlue)))
                .CornerRadius(ControlCornerRadiusValue)
                .Padding(6, 1, 6, 2)
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

        // 图标只有 12px，命中区太小，点在图标边缘会落到卡片上，看起来就是「点了没反应」，
        // 所以命中区放大到 36×32；命令在创建时就绑一次，避免只靠 update 赋值时漏绑.
        var replyButton = new XamlHostElement(
            () =>
            {
                var button = CreateReplyButton();
                button.Width = 36;
                button.Height = 32;
                button.Command = comment.MarkReplyTargetCommand;
                return button;
            },
            element =>
            {
                var button = (Button)element;
                button.Width = 36;
                button.Height = 32;
                button.Command = comment.MarkReplyTargetCommand;
            })
            .Width(36)
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

    /// <summary>
    /// 返回上一级：箭头图标按钮.
    /// </summary>
    private static Element CreateBackButton(CommentMainViewModel viewModel)
        => new XamlHostElement(
            () =>
            {
                var button = new Button
                {
                    Content = new FluentIcons.WinUI.SymbolIcon
                    {
                        Symbol = FluentIcons.Common.Symbol.ArrowLeft,
                        FontSize = 16,
                    },
                    Width = 40,
                    Height = 40,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(8),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                ToolTipService.SetToolTip(button, "返回上一级");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "返回上一级");
                button.Click += (_, _) => viewModel.BackToMainCommand.Execute(default);
                return button;
            },
            _ => { })
            .HAlign(HorizontalAlignment.Left);

    private static Button CreateReplyButton()    {
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




