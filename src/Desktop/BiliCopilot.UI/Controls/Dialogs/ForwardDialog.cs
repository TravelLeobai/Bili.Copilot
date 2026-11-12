// Copyright (c) Bili Copilot. All rights reserved.

using System.Text;
using BiliCopilot.UI.Controls.Components;
using BiliCopilot.UI.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

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
/// 转发动态的输入框：标题 + 说点什么吧 + 缩略卡片 + 表情面板 + 字数 + 发表/取消.
/// 输入框用 RichEditBox：表情以图片形式内嵌，发表时再还原成 [emote] 文本.
/// </summary>
public static class ForwardDialog
{
    /// <summary>
    /// 当前可用的 XamlRoot（由承载按钮的控件在 Loaded 时写入）.
    /// </summary>
    public static XamlRoot? CurrentRoot { get; set; }

    private static readonly HttpClient ImageClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// 弹出转发框.
    /// </summary>
    /// <param name="xamlRoot">当前窗口的 XamlRoot.</param>
    /// <param name="preview">被转发的对象（用于画缩略卡片）.</param>
    /// <returns>用户输入的文字（取消时为 null）+ 是否允许评论和转发.</returns>
    public static async Task<(string? Text, bool AllowInteract)> ShowAsync(XamlRoot? xamlRoot, ForwardPreview? preview)
    {
        var root = xamlRoot ?? CurrentRoot;
        if (root is null)
        {
            return (null, true);
        }

        var box = new RichEditBox
        {
            PlaceholderText = "说点什么吧...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 130,
            MaxLength = 1000,
        };

        // 插进去的表情 token，按顺序记下来；发表时把文档里的图片占位符换回 token.
        var tokens = new List<string>();

        var counter = new TextBlock
        {
            Text = "0",
            FontSize = 12,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        box.TextChanged += (_, _) => counter.Text = ReadPlain(box, tokens).Length.ToString();

        var panel = new StackPanel { Spacing = 10, Width = 560 };
        panel.Children.Add(box);

        var emojiButton = new Button
        {
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FluentIcons.WinUI.SymbolIcon
            {
                Symbol = FluentIcons.Common.Symbol.Emoji,
                FontSize = 14,
            },
        };
        ToolTipService.SetToolTip(emojiButton, "表情");
        emojiButton.Flyout = BuildEmoteFlyout(box, tokens);

        var toolbar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        toolbar.Children.Add(emojiButton);

        // 右半边：允许评论和转发 + 字数，整体靠右.
        // 勾选框和文字分开摆：CheckBox 自带 Content 时文字基线跟方框对不齐，
        // 这里把方框和 TextBlock 各自垂直居中、再并成一行.
        var allowBox = new CheckBox
        {
            IsChecked = true,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(allowBox, "允许评论和转发");

        var allowText = new TextBlock
        {
            Text = "允许评论和转发",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        // 点文字也能切换，别让用户非得瞄准那个小方框.
        allowText.Tapped += (_, e) =>
        {
            allowBox.IsChecked = allowBox.IsChecked != true;
            e.Handled = true;
        };

        var allowGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0,
        };
        allowGroup.Children.Add(allowBox);
        allowGroup.Children.Add(allowText);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(allowGroup);
        right.Children.Add(counter);
        Grid.SetColumn(right, 1);
        toolbar.Children.Add(right);
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
        if (result != ContentDialogResult.Primary)
        {
            return (null, true);
        }

        var allowInteract = allowBox.IsChecked == true;
        return (ReadPlain(box, tokens).Trim(), allowInteract);
    }

    /// <summary>
    /// 读出正文：文档里的图片占位符按插入顺序替换回表情 token.
    /// </summary>
    private static string ReadPlain(RichEditBox box, List<string> tokens)
    {
        box.Document.GetText(TextGetOptions.NoHidden, out var text);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = text.TrimEnd('\r');
        var builder = new StringBuilder(text.Length + (tokens.Count * 8));
        var index = 0;
        foreach (var ch in text)
        {
            // RichEdit 里内嵌对象读出来是 U+FFFC（对象替换符）.
            if (ch == '\uFFFC')
            {
                builder.Append(index < tokens.Count ? tokens[index] : string.Empty);
                index++;
            }
            else
            {
                builder.Append(ch);
            }
        }

        // 多出来的 token（比如用户删了图片）直接忽略，避免重复.
        return builder.ToString();
    }

    private static Flyout BuildEmoteFlyout(RichEditBox box, List<string> tokens)
    {
        var host = new Grid { Width = 320, Height = 420 };
        var flyout = new Flyout
        {
            Content = host,
        };

        flyout.Opened += (_, _) =>
        {
            if (host.Children.Count > 0)
            {
                return;
            }

            var emotePanel = new EmotePanel
            {
                Width = 320,
                Height = 420,
            };
            emotePanel.ItemClick += async (_, key) => await InsertEmoteAsync(box, tokens, key);
            host.Children.Add(emotePanel);
        };

        return flyout;
    }

    private static async Task InsertEmoteAsync(RichEditBox box, List<string> tokens, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var emote = BiliEmoteService.TryGet(key);
        var imageUri = string.IsNullOrWhiteSpace(emote?.Url) ? null : new Uri(emote!.Url);
        if (imageUri is null)
        {
            // 拿不到图就退回文字 token，至少不丢内容.
            InsertPlain(box, key);
            tokens.Add(key);
            return;
        }

        byte[]? bytes = null;
        try
        {
            using var response = await ImageClient.GetAsync(imageUri);
            if (response.IsSuccessStatusCode)
            {
                bytes = await response.Content.ReadAsByteArrayAsync();
            }
        }
        catch
        {
            bytes = null;
        }

        if (bytes is null || bytes.Length == 0)
        {
            InsertPlain(box, key);
            tokens.Add(key);
            return;
        }

        var (width, height) = GetEmoteSize(emote);
        InsertPicture(box, bytes, width, height);
        tokens.Add(key);
    }

    private static (int Width, int Height) GetEmoteSize(BiliEmote? emote)
    {
        // 面板没给像素尺寸，按档位估：普通 24，大表情 36.
        var side = emote?.Size == 2 ? 36 : 24;
        return (side, side);
    }

    /// <summary>
    /// 把图片插到光标处：RichEdit 的 ITextRange 没有 InsertImage，只能整篇按 RTF 重建.
    /// </summary>
    private static void InsertPicture(RichEditBox box, byte[] imageBytes, int width, int height)
    {
        box.Document.GetText(TextGetOptions.NoHidden, out var raw);
        raw = raw.TrimEnd('\r');

        var caret = box.Document.Selection.StartPosition;
        caret = Math.Clamp(caret, 0, raw.Length);

        var before = EscapeRtf(raw[..caret]);
        var after = EscapeRtf(raw[caret..]);
        var hex = Convert.ToHexString(imageBytes);
        var pict = $@"{{\pict\pngblip\picw{width * 15}\pich{height * 15}\picwgoal{width * 15}\pichgoal{height * 15} {hex}}}";
        var rtf = $@"{{\rtf1\ansi\deff0{{\fonttbl{{\f0 Segoe UI;}}}}\f0\fs22 {before}{pict}{after}}}";

        box.Document.SetText(TextSetOptions.FormatRtf, rtf);
        box.Document.Selection.SetRange(caret + 1, caret + 1);
    }

    private static void InsertPlain(RichEditBox box, string text)
    {
        box.Document.GetText(TextGetOptions.NoHidden, out var raw);
        raw = raw.TrimEnd('\r');
        var caret = Math.Clamp(box.Document.Selection.StartPosition, 0, raw.Length);
        box.Document.SetText(TextSetOptions.None, raw.Insert(caret, text));
        box.Document.Selection.SetRange(caret + text.Length, caret + text.Length);
    }

    private static string EscapeRtf(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace("\r", "\\par ")
            .Replace("\n", "\\par ");

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

