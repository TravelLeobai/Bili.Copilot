// Copyright (c) Bili Copilot. All rights reserved.

using System.Text;
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
/// 转发动态的输入框：标题 + 说点什么吧 + 缩略卡片 + 字数 + 发表/取消.
/// <para>
/// 表情面板已摘掉：原来那套"图片内嵌 + 整篇重建 RTF"的做法会把正文里的中文一起弄坏，
/// 发表后表情连同评论一起消失。要发表情就走 B 站网页端，这里只保证文字能正常发出去.
/// </para>
/// </summary>
public static class ForwardDialog
{
    /// <summary>
    /// 当前可用的 XamlRoot（由承载按钮的控件在 Loaded 时写入）.
    /// </summary>
    public static XamlRoot? CurrentRoot { get; set; }

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


        var counter = new TextBlock
        {
            Text = "0",
            FontSize = 12,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        box.TextChanged += (_, _) => counter.Text = ReadPlain(box).Length.ToString();

        var panel = new StackPanel { Spacing = 10, Width = 560 };
        panel.Children.Add(box);

        var toolbar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };

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
        return (ReadPlain(box).Trim(), allowInteract);
    }

    /// <summary>
    /// 读出正文。表情现在是纯文本 <c>[名字]</c>，直接取文档文本即可.
    /// </summary>
    private static string ReadPlain(RichEditBox box)
    {
        box.Document.GetText(TextGetOptions.NoHidden, out var text);
        return string.IsNullOrEmpty(text) ? string.Empty : text.TrimEnd('\r');
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

