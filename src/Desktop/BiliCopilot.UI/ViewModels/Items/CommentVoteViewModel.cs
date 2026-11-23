// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using System.Globalization;
using BiliCopilot.UI.Services;
using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.WinUIKernel.Share.Base;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 评论附带的投票：正文下面那一行入口，以及打开之后的面板.
/// <para>
/// 状态都摆在 <see cref="Phase"/> 上：加载中 / 可投 / 已投 / 已结束 / 不可用 / 加载失败.
/// 异步结果回来时会比一次「代际」，旧任务不许覆盖新数据（同一条评论被重绘、被回收都可能出现旧任务）.
/// </para>
/// </summary>
public sealed partial class CommentVoteViewModel : ObservableObject
{
    // 没有真正截止时间的投票会带一个几十年后的时间，这种日子不值得显示.
    private static readonly TimeSpan FarDeadline = TimeSpan.FromDays(3650);

    private readonly CommentVoteRef _vote;
    private readonly CommentVoteService _service;
    private readonly Func<string> _cookieProvider;
    private readonly ILogger<CommentVoteViewModel> _logger;

    private CommentVoteDetail? _detail;
    private int _loadGeneration;
    private bool _justVoted;
    private bool _isDetached;
    private CancellationTokenSource? _loadCts;
    private string? _failure;
    private string? _failureReason;

    internal CommentVoteViewModel(CommentVoteRef vote, CommentVoteService service, Func<string> cookieProvider)
    {
        _vote = vote;
        _service = service;
        _cookieProvider = cookieProvider;
        _logger = this.Get<ILogger<CommentVoteViewModel>>();
        EntryTitle = EntryCaption = EntryName = string.Empty;
        Description = Meta = Status = string.Empty;
        Title = string.IsNullOrWhiteSpace(vote.Title) ? "投票" : vote.Title!;
        UpdateEntry(vote.Title, vote.ParticipantCount);
    }

    /// <summary>
    /// Gets 选项。详情回来之前是空的.
    /// </summary>
    public ObservableCollection<CommentVoteOptionViewModel> Options { get; } = [];

    /// <summary>
    /// Gets a value indicating whether 正在拉详情，而且还没有旧数据可以显示.
    /// </summary>
    public bool IsLoading => Phase == CommentVotePhase.Loading;

    /// <summary>
    /// Gets a value indicating whether 详情已经拿到（可以显示选项了）.
    /// </summary>
    public bool IsReady => Phase == CommentVotePhase.Ready;

    /// <summary>
    /// Gets a value indicating whether 加载失败（可以重试）.
    /// </summary>
    public bool IsFailed => Phase == CommentVotePhase.Failed;

    /// <summary>
    /// Gets a value indicating whether 投票不存在或不可见.
    /// </summary>
    public bool IsUnavailable => Phase == CommentVotePhase.Unavailable;

    /// <summary>
    /// Gets a value indicating whether 入口有第二行.
    /// </summary>
    public bool HasEntryCaption => EntryCaption.Length > 0;

    /// <summary>
    /// Gets a value indicating whether 发起人写了说明.
    /// </summary>
    public bool HasDescription => Description.Length > 0;

    /// <summary>
    /// Gets a value indicating whether 有「单选/多选 · 人数 · 截止」那一行.
    /// </summary>
    public bool HasMeta => Meta.Length > 0;

    /// <summary>
    /// Gets a value indicating whether 选项下面有话说.
    /// </summary>
    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// Gets or sets 入口第一行：投票标题，没有标题就是「投票」.
    /// </summary>
    [ObservableProperty]
    private string _entryTitle;

    /// <summary>
    /// Gets or sets 入口第二行.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntryCaption))]
    private string _entryCaption;

    /// <summary>
    /// Gets or sets 读屏念入口时说的话.
    /// </summary>
    [ObservableProperty]
    private string _entryName;

    /// <summary>
    /// Gets or sets 面板现在是什么状态.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsFailed), nameof(IsUnavailable))]
    private CommentVotePhase _phase;

    /// <summary>
    /// Gets or sets 投票标题.
    /// </summary>
    [ObservableProperty]
    private string _title;

    /// <summary>
    /// Gets or sets 发起人的说明.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string _description;

    /// <summary>
    /// Gets or sets 「单选/多选 · 人数 · 截止」那一行.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeta))]
    private string _meta;

    /// <summary>
    /// Gets or sets a value indicating whether 现在能勾选并提交；否则显示结果.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isChoosing;

    /// <summary>
    /// Gets or sets a value indicating whether 正在提交.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isSubmitting;

    /// <summary>
    /// Gets or sets a value indicating whether 匿名投票.
    /// </summary>
    [ObservableProperty]
    private bool _isAnonymous;

    /// <summary>
    /// Gets or sets 选项下面那行字：还能选几项、正在提交、哪里失败了.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status;

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="Status"/> 说的是「没投成」.
    /// </summary>
    [ObservableProperty]
    private bool _isStatusFailure;

    /// <summary>
    /// 勾选/取消一个选项。单选时是互斥的，多选选满了就不许再勾.
    /// </summary>
    /// <param name="option">选项.</param>
    public void ToggleOption(CommentVoteOptionViewModel? option)
    {
        if (!IsChoosing || IsSubmitting || _detail is not { } detail || option is null)
        {
            return;
        }

        if (option.IsChecked)
        {
            option.IsChecked = false;
        }
        else if (detail.MaxChoiceCount <= 1)
        {
            foreach (var other in Options)
            {
                other.IsChecked = ReferenceEquals(other, option);
            }
        }
        else if (Options.Count(item => item.IsChecked) < detail.MaxChoiceCount)
        {
            option.IsChecked = true;
        }

        ClearFailure();
        UpdateRows();
        UpdateStatus();
        SubmitCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 卡片被回收、面板被关掉时调用：在途的详情请求作废，回来的结果不再往已经没了的 UI 上写.
    /// <para>
    /// 提交中的请求故意不取消：票可能已经发出去了，此时掐断请求只会让结果无从知晓.
    /// </para>
    /// </summary>
    public void Detach()
    {
        _isDetached = true;
        _loadGeneration++;
        CancelPendingLoads();
    }

    /// <summary>
    /// 面板重新挂上来（同一个投票的弹出被再次打开）.
    /// </summary>
    public void Reattach() => _isDetached = false;

    /// <summary>
    /// 拉一次详情。打开弹出时才调，滚动列表不调（不然就是 N+1 请求）.
    /// </summary>
    /// <returns>任务.</returns>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        var generation = ++_loadGeneration;
        var hadDetail = _detail is not null;
        if (!hadDetail)
        {
            Phase = CommentVotePhase.Loading;
        }

        // 上一次的详情请求作废：它回来的结果不许覆盖这一次.
        CancelPendingLoads();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        try
        {
            var detail = await _service.GetDetailAsync(_vote.Id, _cookieProvider(), token);
            if (generation != _loadGeneration || IsSubmitting)
            {
                return;
            }

            if (detail is null)
            {
                _detail = null;
                Options.Clear();
                IsChoosing = false;
                Phase = CommentVotePhase.Unavailable;
                return;
            }

            Apply(detail, keepSelection: hadDetail);
            Phase = CommentVotePhase.Ready;
        }
        catch (OperationCanceledException)
        {
            // 面板关了/卡片被回收，正常路径.
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            // 只记异常类型：这里的信息不该带着 url 上路.
            _logger.LogWarning("评论投票详情加载失败：{ErrorType}", ex.GetType().Name);
            if (!hadDetail)
            {
                Phase = CommentVotePhase.Failed;
            }
        }
    }

    /// <summary>
    /// 提交投票。只有用户点了「投票」才会走到这里.
    /// </summary>
    /// <returns>任务.</returns>
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (!CanSubmit() || _detail is not { } detail)
        {
            return;
        }

        if (detail.HasEnded)
        {
            // 面板开着的时候截止时间过了.
            Apply(detail, keepSelection: false);
            return;
        }

        var chosen = Options.Where(item => item.IsChecked).Select(item => item.Index).ToList();
        var voterId = GetSignedInUserId();
        ClearFailure();

        // 提交期间不再接受「重新加载」，避免结果被覆盖.
        _loadGeneration++;
        CancelPendingLoads();
        IsSubmitting = true;
        UpdateRows();
        UpdateStatus();
        try
        {
            var result = await _service.SubmitAsync(detail, chosen, voterId ?? string.Empty, IsAnonymous, _cookieProvider(), CancellationToken.None);
            _justVoted = true;

            // 先出结果，再收掉「正在提交」：面板靠这个顺序挪键盘焦点.
            Apply(result, keepSelection: false);
            IsSubmitting = false;
            UpdateRows();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            IsSubmitting = false;
            (_failure, _failureReason) = ex switch
            {
                UnauthorizedAccessException => ("登录后才能投票", default(string)),
                CommentVoteRejectedException { IsSupporterOnly: true } => ("充电专属投票，给 UP 主充电后才能参与", null),
                CommentVoteRejectedException { Reason: { Length: > 0 } reason } => ($"投票没有成功：{reason}", null),
                CommentVoteForbiddenException => ("投票请求被平台拦截，请稍后再试", null),
                _ => ("投票没有成功", null),
            };
            _logger.LogWarning("评论投票提交失败：{ErrorType}", ex.GetType().Name);
            UpdateRows();
            UpdateStatus();

            if (ex is CommentVoteRejectedException)
            {
                // 服务端知道一些面板还不知道的事（已经投过、已经关掉）：重新拉一次，
                // 但原因要继续留在屏幕上，只要看起来还能再投.
                await LoadAsync();
            }
        }
    }

    private bool CanSubmit() => IsChoosing && !IsSubmitting && Options.Any(item => item.IsChecked);

    private string? GetSignedInUserId()
        => this.Get<IBiliTokenResolver>().GetToken() is { UserId: > 0 } token
            ? token.UserId.ToString(CultureInfo.InvariantCulture)
            : null;

    private void Apply(CommentVoteDetail detail, bool keepSelection)
    {
        if (_isDetached)
        {
            return;
        }

        var isMulti = detail.MaxChoiceCount > 1;
        var previous = keepSelection && _detail is not null
            ? Options.Where(item => item.IsChecked).Select(item => item.Index).ToHashSet()
            : [];
        var sameOptions = _detail is not null
            && Options.Count == detail.Options.Count
            && _detail.MaxChoiceCount == detail.MaxChoiceCount
            && Options.Select(item => (item.Index, item.Text, item.Image)).SequenceEqual(detail.Options.Select(item => (item.Index, item.Text, item.Image)));
        _detail = detail;

        if (sameOptions)
        {
            // 只是票数动了：保留行本身，键盘焦点和滚动位置都不会丢.
            for (var i = 0; i < Options.Count && i < detail.Options.Count; i++)
            {
                Options[i].Count = detail.Options[i].Count;
                Options[i].IsChecked = previous.Contains(Options[i].Index);
            }
        }
        else
        {
            Options.Clear();
            foreach (var option in detail.Options)
            {
                Options.Add(new CommentVoteOptionViewModel(option, isMulti) { IsChecked = previous.Contains(option.Index) });
            }
        }

        var userId = GetSignedInUserId();
        var isMine = userId is not null && userId == detail.PublisherId;
        IsChoosing = userId is not null && !isMine && detail.MyVotes.Count == 0 && !detail.HasEnded;
        if (!IsChoosing)
        {
            ClearFailure();
        }

        Title = detail.Title;
        Description = detail.Description ?? string.Empty;
        UpdateEntry(detail.Title, detail.ParticipantCount);
        UpdateMeta(detail);
        UpdateRows();
        UpdateStatus();
        SubmitCommand.NotifyCanExecuteChanged();
    }

    private void UpdateEntry(string? title, long? participantCount)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var hasCount = participantCount is > 0;
        var people = hasCount ? $"{AppToolkit.FormatCount(participantCount!.Value)} 人参与" : string.Empty;
        EntryTitle = hasTitle ? title! : "投票";
        EntryCaption = hasTitle ? (hasCount ? $"投票 · {people}" : "投票") : people;
        EntryName = hasTitle
            ? (hasCount ? $"投票：{title}，{Number(participantCount!.Value)} 人参与" : $"投票：{title}")
            : (hasCount ? $"投票，{people}" : "投票");
    }

    private void UpdateMeta(CommentVoteDetail detail)
    {
        var parts = new List<string>
        {
            detail.MaxChoiceCount > 1 ? $"最多选 {detail.MaxChoiceCount} 项" : "单选",
            $"{Number(detail.ParticipantCount)} 人参与",
        };

        if (detail.HasEnded)
        {
            parts.Add("已结束");
        }
        else if (detail.EndTime is { } end && end - DateTimeOffset.UtcNow < FarDeadline)
        {
            parts.Add($"{end.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} 截止");
        }

        Meta = string.Join(" · ", parts);
    }

    private void UpdateRows()
    {
        if (_detail is not { } detail)
        {
            return;
        }

        // 占比按总票数算（和官方页面一致）；多选投票的总票数会大于参与人数.
        long total = 0;
        foreach (var option in Options)
        {
            total += Math.Max(0, option.Count);
        }

        var checkedCount = Options.Count(item => item.IsChecked);
        var isFull = detail.MaxChoiceCount > 1 && checkedCount >= detail.MaxChoiceCount;
        foreach (var option in Options)
        {
            var percent = total > 0 ? Math.Round(option.Count * 100d / total) : 0;
            var count = Number(option.Count);
            option.IsMine = detail.MyVotes.Contains(option.Index);
            option.Percent = percent;
            option.Stats = $"{count} 票 · {percent}%";
            option.ResultName = string.Format(
                CultureInfo.CurrentCulture,
                option.IsMine ? "选项 {0}，{1} 票，占 {2}%，你选的" : "选项 {0}，{1} 票，占 {2}%",
                option.Text,
                count,
                percent);
            option.CanChoose = IsChoosing && !IsSubmitting && (option.IsChecked || !isFull);
            option.IsChoice = IsChoosing;
        }
    }

    private void UpdateStatus()
    {
        if (_detail is not { } detail)
        {
            Status = string.Empty;
            IsStatusFailure = false;
            return;
        }

        var failed = false;
        string status;
        if (IsSubmitting)
        {
            status = "正在提交…";
        }
        else if (IsChoosing)
        {
            if (_failure is { } failure)
            {
                failed = true;
                status = _failureReason is { Length: > 0 } reason ? string.Format(CultureInfo.CurrentCulture, failure, reason) : failure;
            }
            else if (detail.MaxChoiceCount > 1)
            {
                var left = detail.MaxChoiceCount - Options.Count(item => item.IsChecked);
                status = left > 0 ? $"还可以选 {left} 项" : $"已选满 {detail.MaxChoiceCount} 项";
            }
            else
            {
                status = detail.IsSupporterOnly ? "充电专属投票，给 UP 主充电后才能参与" : string.Empty;
            }
        }
        else if (_justVoted)
        {
            status = "已投票";
        }
        else if (detail.HasEnded || detail.MyVotes.Count > 0)
        {
            status = string.Empty;
        }
        else
        {
            var userId = GetSignedInUserId();
            status = userId is null
                ? "登录后才能投票"
                : userId == detail.PublisherId ? "这是你发起的投票" : string.Empty;
        }

        IsStatusFailure = failed;
        Status = status;
    }

    private void CancelPendingLoads()
    {
        try
        {
            _loadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过了，没有在途请求可取消.
        }
    }

    private void ClearFailure()
    {
        _failure = null;
        _failureReason = null;
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// 评论投票面板现在显示什么.
/// </summary>
public enum CommentVotePhase
{
    /// <summary>
    /// 还没打开过.
    /// </summary>
    Idle,

    /// <summary>
    /// 正在拉详情，什么都还没有.
    /// </summary>
    Loading,

    /// <summary>
    /// 详情在屏幕上.
    /// </summary>
    Ready,

    /// <summary>
    /// 拉不到详情，可以重试.
    /// </summary>
    Failed,

    /// <summary>
    /// 投票已删除或者不给看.
    /// </summary>
    Unavailable,
}
