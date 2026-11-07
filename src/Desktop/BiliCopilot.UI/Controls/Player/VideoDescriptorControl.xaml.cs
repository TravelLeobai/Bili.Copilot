// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using Richasy.BiliKernel.Models.Base;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 视频描述组件.
/// </summary>
public sealed partial class VideoDescriptorControl : VideoPlayerControlBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VideoDescriptorControl"/> class.
    /// </summary>
    public VideoDescriptorControl() => InitializeComponent();

    private void OnTagButtonClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BiliTag tag
            || string.IsNullOrWhiteSpace(tag.Name))
        {
            return;
        }

        // SearchBoxViewModel.SearchCommand 的参数是 SearchSuggestItemViewModel，
        // 这里原来直接把 tag 名字符串丢进去，Execute 内部转换失败、命令等于没执行
        // （表现就是点 tag 没反应）。改成走导航层的搜索：它在已经打开搜索页时直接换关键词，
        // 否则新开搜索页，拿到的就是当前 tag 词.
        Serilog.Log.Information("视频标签被点击，跳搜索：{Tag}", tag.Name);
        this.Get<NavigationViewModel>().Search(tag.Name);
    }
}
