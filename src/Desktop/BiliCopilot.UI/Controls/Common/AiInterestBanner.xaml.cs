using System.Linq;
// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Controls.Common;

/// <summary>
/// 收藏 / 历史 / 稍后再看 顶部的「AI 分析」圆角条：左边图标，右边一段话。
/// 没有可分析的数据、或没配 AI 接口时自动隐藏，不占位.
/// </summary>
public sealed partial class AiInterestBanner : UserControl
{
    private string _pageKey = string.Empty;
    private bool _loading;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiInterestBanner"/> class.
    /// </summary>
    public AiInterestBanner() => InitializeComponent();

    /// <summary>
    /// 刷新分析：先显示缓存，没有就发一次请求.
    /// </summary>
    /// <param name="pageKey">页面标识.</param>
    /// <param name="videos">这一页的视频列表.</param>
    /// <param name="logger">日志.</param>
    /// <returns>任务.</returns>
    public async Task RefreshAsync(string pageKey, IEnumerable<VideoItemViewModel> videos, ILogger? logger)
    {
        _pageKey = pageKey;
        var videoCount = videos?.Count() ?? 0;
        Serilog.Log.Information(
            "AI 兴趣条：page={Key} enabled={Enabled} configured={Configured} items={Count}",
            pageKey,
            AiInterestAnalyzer.IsEnabled,
            AiInterestAnalyzer.IsConfigured,
            videoCount);

        if (!AiInterestAnalyzer.IsEnabled || !AiInterestAnalyzer.IsConfigured)
        {
            RootBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var cached = PageInterestAnalyzer.GetCached(pageKey);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            Show(cached);
            return;
        }

        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            TextLabel.Text = "正在分析最近的观看兴趣…";
            RootBorder.Visibility = Visibility.Visible;
            var text = await PageInterestAnalyzer.AnalyzeAsync(pageKey, videos, logger);
            if (string.IsNullOrWhiteSpace(text))
            {
                RootBorder.Visibility = Visibility.Collapsed;
                return;
            }

            Show(text);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "页面兴趣分析失败：{Key}", pageKey);
            RootBorder.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(string text)
    {
        TextLabel.Text = text;
        RootBorder.Visibility = Visibility.Visible;
    }
}
