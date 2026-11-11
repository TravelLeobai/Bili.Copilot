// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.Controls.Dialogs;

/// <summary>
/// 转发动态的输入框：标题 + 说点什么吧 + @作者 提示 + 字数 + 发表/取消.
/// 布局照 B 站网页那块来，返回用户输入的文字（取消返回 null）.
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
    /// <param name="atUser">被转发的内容作者（只做提示，不参与发送）.</param>
    /// <returns>用户输入的文字；取消返回 null.</returns>
    public static async Task<string?> ShowAsync(XamlRoot? xamlRoot, string? atUser)
    {
        var box = new TextBox
        {
            PlaceholderText = "说点什么吧...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 140,
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

        if (!string.IsNullOrWhiteSpace(atUser))
        {
            panel.Children.Add(
                new Border
                {
                    Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Child = new TextBlock
                    {
                        Text = atUser,
                        FontSize = 12,
                        Opacity = 0.8,
                    },
                });
        }

        panel.Children.Add(counter);

        var root = xamlRoot ?? CurrentRoot;
        if (root is null)
        {
            return null;
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
}

