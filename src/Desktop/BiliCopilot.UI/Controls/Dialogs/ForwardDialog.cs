// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BiliCopilot.UI.Controls.Dialogs;

/// <summary>
/// 弹框里那张缩略卡片的数据：头像 + 昵称 + 封面（可空）+ 标题或正文.
/// </summary>
/// <param name="Name">作者昵称.</param>
/// <param name="Avatar">头像地址.</param>
/// <param name="Cover">封面地址，动态纯文字时为空.</param>
/// <param name="Text">视频标题或动态正文.</param>
public sealed record ForwardPreview(string? Name, Uri? Avatar, Uri? Cover, string Text);

/// <summary>
/// 转发动态的输入框：标题 + 说点什么吧 + 缩略卡片 + 表情 + 字数 + 发表/取消.
/// </summary>
public static class ForwardDialog
{
    /// <summary>
    /// 当前可用的 XamlRoot（由承载按钮的控件在 Loaded 时写入）.
    /// </summary>
    public static XamlRoot? CurrentRoot { get; set; }

    private static readonly string[] Emojis =
    [
        "😂", "🤣", "😭", "😍", "🥰", "😘", "😎", "🤔",
        "😅", "😳", "🙃", "😴", "🤤", "😡", "🤯", "🥺",
        "👍", "👎", "🙏", "👏", "💪", "🤝", "✌️", "🫡",
        "❤️", "💔", "🔥", "✨", "🎉", "🍋", "🌹", "🐶",
    ];

    /// <summary>
    /// 弹出转发框.
    /// </summary>
    /// <param name="xamlRoot">当前窗口的 XamlRoot.</param>
    /// <param name="preview">被转发的对象（用于画缩略卡片）.</param>
    /// <returns>用户输入的文字；取消返回 null.</returns>
    public static async Task<string?> ShowAsync(XamlRoot? xamlRoot, ForwardPreview? preview)
    {
        var root = xamlRoot ?? CurrentRoot;
        if (root is null)
        {
            return null;
        }

        var box = new TextBox
        {
            PlaceholderText = "说点什么吧...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 130,
            MaxLength = 1000,
        };

        var counter = new TextBlock
        {
            Text = "0",
            FontSize = 12,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        box.TextChanged += (_, _) => counter.Text = box.Text?.Length.ToString() ?? "0";

        var panel = new StackPanel { Spacing = 10, Width = 520 };
        panel.Children.Add(box);

        // 工具条：表情按钮 + 字数.
        var emojiButton = new Button
        {
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new FluentIcons.WinUI.SymbolIcon
            {
                Symbol = FluentIcons.Common.Symbol.Emoji,
                FontSize = 14,
            },
        };
        ToolTipService.SetToolTip(emojiButton, "表情");
        emojiButton.Flyout = BuildEmojiFlyout(box);

        var toolbar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        toolbar.Children.Add(emojiButton);
        Grid.SetColumn(counter, 1);
        toolbar.Children.Add(counter);
        panel.Children.Add(toolbar);

        if (preview is not null)
        {
            panel.Children.Add(BuildPreviewCard(preview));
        }

        var dialog = new ContentDialog
        {
            Title = "转发动态",
            Content = panel,
            PrimaryButtonText = "发表",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text ?? string.Empty : null;
    }

    private static Flyout BuildEmojiFlyout(TextBox box)
    {
        var grid = new Grid
        {
            ColumnSpacing = 2,
            RowSpacing = 2,
        };
        for (var i = 0; i < 8; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var i = 0; i < 4; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < Emojis.Length; i++)
        {
            var emoji = Emojis[i];
            var button = new Button
            {
                Content = emoji,
                FontSize = 18,
                Padding = new Thickness(6, 2, 6, 2),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            ToolTipService.SetToolTip(button, emoji);
            button.Click += (_, _) =>
            {
                var caret = box.SelectionStart;
                box.Text = box.Text.Insert(caret, emoji);
                box.SelectionStart = caret + emoji.Length;
            };
            Grid.SetRow(button, i / 8);
            Grid.SetColumn(button, i % 8);
            grid.Children.Add(button);
        }

        return new Flyout { Content = grid };
    }

    private static FrameworkElement BuildPreviewCard(ForwardPreview preview)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };

        header.Children.Add(new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = preview.Avatar is null ? null : BuildImageBrush(preview.Avatar, 56),
        });

        header.Children.Add(new TextBlock
        {
            Text = preview.Name ?? "未知作者",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var body = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(0, 8, 0, 0),
        };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var text = new TextBlock
        {
            Text = preview.Text,
            FontSize = 12,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (preview.Cover is not null)
        {
            var cover = new Border
            {
                Width = 96,
                Height = 54,
                CornerRadius = new CornerRadius(6),
                Background = BuildImageBrush(preview.Cover, 192),
            };
            body.Children.Add(cover);
            Grid.SetColumn(cover, 0);
            Grid.SetColumn(text, 1);
        }
        else
        {
            // 没图时正文与昵称左对齐，不空出封面那一格.
            Grid.SetColumn(text, 0);
            Grid.SetColumnSpan(text, 2);
        }

        body.Children.Add(text);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(body);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = content,
        };
    }

    private static ImageBrush BuildImageBrush(Uri uri, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        if (decodeWidth > 0)
        {
            bitmap.DecodePixelWidth = decodeWidth;
        }

        bitmap.UriSource = uri;
        return new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
    }
}
