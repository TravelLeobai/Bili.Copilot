// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;

namespace BiliCopilot.UI.Controls.Dialogs;

/// <summary>
/// TV 扫码授权弹框：先拿二维码画出来，然后每 2 秒问一次扫码结果.
/// 只补一个 <c>access_key</c>，不参与原有登录；关掉弹框就停止轮询.
/// </summary>
public static class TvAuthDialog
{
    private const int QrSize = 240;
    private const int PixelsPerModule = 6;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 弹出扫码授权框，等到扫码成功或用户取消.
    /// </summary>
    /// <param name="xamlRoot">当前窗口的 XamlRoot.</param>
    /// <returns>是否拿到了授权.</returns>
    public static async Task<bool> ShowAsync(XamlRoot? xamlRoot)
    {
        var root = xamlRoot ?? ForwardDialog.CurrentRoot;
        if (root is null)
        {
            Serilog.Log.Warning("拿不到 XamlRoot，TV 扫码框不弹");
            return false;
        }

        var image = new Image
        {
            Width = QrSize,
            Height = QrSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var status = new TextBlock
        {
            Text = "正在获取二维码…",
            FontSize = 13,
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };

        var panel = new StackPanel
        {
            Spacing = 12,
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(image);
        panel.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "扫码授权",
            Content = panel,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };

        var session = new TvAuthSession();
        using var cancellation = new CancellationTokenSource();
        dialog.Closed += (_, _) => session.IsClosed = true;

        // 先跑起来（第一句是拿二维码），再 ShowAsync；关框时取消并把后台流程收干净.
        var workflow = RunAsync(image, status, dialog, session, cancellation.Token);
        await dialog.ShowAsync();
        if (!cancellation.IsCancellationRequested)
        {
            await cancellation.CancelAsync();
        }

        try
        {
            await workflow;
        }
        catch (OperationCanceledException)
        {
        }

        return session.IsAuthorized;
    }

    private static async Task RunAsync(
        Image image,
        TextBlock status,
        ContentDialog dialog,
        TvAuthSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var (authCode, url) = await BiliOAuthService.CreateQrAsync(cancellationToken);
            await RenderQrAsync(image, url);
            status.Text = "请用手机 B 站扫码";

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = await BiliOAuthService.PollAsync(authCode, cancellationToken);
                if (!string.IsNullOrEmpty(token))
                {
                    session.IsAuthorized = true;
                    status.Text = "已授权";
                    HideIfOpen(dialog, session);
                    return;
                }

                if (BiliOAuthService.LastPollCode == BiliOAuthService.PollCodeExpired)
                {
                    // 过期了就别再问了，让用户关掉重开.
                    status.Text = "二维码已过期";
                    Serilog.Log.Information("TV 扫码：二维码过期，停止轮询");
                    return;
                }

                status.Text = BiliOAuthService.LastPollCode == BiliOAuthService.PollCodeScanned
                    ? "已扫码，请在手机上确认"
                    : "未扫码";
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "TV 扫码授权失败");
            status.Text = $"出错了：{ex.Message}";
        }
    }

    /// <summary>
    /// 按 BiliUWP 的做法生成 PNG 字节，再交给 BitmapImage（和启动页的二维码一个路子）.
    /// </summary>
    private static async Task RenderQrAsync(Image image, string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(PixelsPerModule);

        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        image.Source = bitmap;
    }

    private static void HideIfOpen(ContentDialog dialog, TvAuthSession session)
    {
        if (session.IsClosed)
        {
            return;
        }

        dialog.Hide();
    }

    /// <summary>
    /// 一次弹框的状态：是否已关、是否已授权.
    /// </summary>
    private sealed class TvAuthSession
    {
        public bool IsClosed { get; set; }

        public bool IsAuthorized { get; set; }
    }
}
