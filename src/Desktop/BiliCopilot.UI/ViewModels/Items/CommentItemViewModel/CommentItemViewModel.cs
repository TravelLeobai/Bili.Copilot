// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Humanizer;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Comment;
using Richasy.BiliKernel.Models.Comment;
using Richasy.WinUIKernel.Share.ViewModels;
using WinRT;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 评论项视图模型.
/// </summary>
[GeneratedBindableCustomProperty]
public sealed partial class CommentItemViewModel : ViewModelBase<CommentInformation>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CommentItemViewModel"/> class.
    /// </summary>
    public CommentItemViewModel(CommentInformation data, ICommentService service, Action<CommentItemViewModel> markReplyTargetAction)
        : base(data)
    {
        _service = service;
        _logger = this.Get<ILogger<CommentItemViewModel>>();
        Level = data.User.Level ?? 0;
        IsTop = data.IsTop;
        IsLiked = data.CommunityInformation.IsLiked;
        LikeCount = Convert.ToInt32(data.CommunityInformation.LikeCount);
        RelativeTime = data.PublishTime.Humanize();
        ActualTime = data.PublishTime.ToString("yyyy-MM-dd HH:mm:ss");
        ChildCount = data.CommunityInformation.ChildCount;
        Avatar = data.User.User.Avatar.Uri;
        UserName = data.User.User.Name;
        IsVip = data.User.IsVip ?? false;
        Content = data.Content;
        Pictures = data.Content?.Pictures?.ToList() ?? [];
        _markReplyTargetAction = markReplyTargetAction;
    }

    [RelayCommand]
    private async Task ToggleLikeAsync()
    {
        var state = !IsLiked;
        try
        {
            await _service.ToggleLikeAsync(state, Data.Id, Data.CommentId, Data.CommentType);
            IsLiked = state;
            LikeCount += state ? 1 : -1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换评论点赞状态时失败");
        }
    }

    [RelayCommand]
    private void MarkReplyTarget()
        => _markReplyTargetAction?.Invoke(this);

    [RelayCommand]
    private void ShowUserSpace()
        => this.Get<NavigationViewModel>().NavigateToOver(typeof(UserSpacePage), Data.User.User);

    /// <summary>
    /// 把这条评论转发到自己的动态（@作者 + 正文摘录）.
    /// </summary>
    /// <param name="xamlRoot">XamlRoot.</param>
    /// <returns>任务.</returns>
    public async Task ForwardAsync(Microsoft.UI.Xaml.XamlRoot? xamlRoot)
    {
        try
        {
            var cookie = this.Get<Richasy.BiliKernel.Bili.Authorization.IBiliCookiesResolver>()?.GetCookieString() ?? string.Empty;
            var excerpt = Content?.Text ?? string.Empty;
            if (excerpt.Length > 120)
            {
                excerpt = excerpt[..120];
            }

            var preview = new Controls.Dialogs.ForwardPreview(UserName, Avatar, null, excerpt);
            var text = await Controls.Dialogs.ForwardDialog.ShowAsync(xamlRoot, preview);
            if (text is null)
            {
                return;
            }

            var content = string.IsNullOrWhiteSpace(text)
                ? $"@{UserName}：{excerpt}"
                : $"{text}\n@{UserName}：{excerpt}";
            var (ok, message) = await Services.DynamicShareService.PostTextAsync(content, cookie, this.Get<ILogger<CommentItemViewModel>>());
            Serilog.Log.Information("评论转发结果：{Ok} {Message}", ok, message);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "评论转发失败");
        }
    }





}
