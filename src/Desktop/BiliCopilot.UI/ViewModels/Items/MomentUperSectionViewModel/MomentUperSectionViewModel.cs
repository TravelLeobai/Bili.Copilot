// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.View;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Moment;
using Richasy.BiliKernel.Models.Moment;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 动态点赞者条目视图模型.
/// </summary>
public sealed partial class MomentUperSectionViewModel : ViewModelBase<MomentProfile>
{
    /// <summary>
    /// 每次往界面补多少张卡片（瀑布流没有虚拟化，一次建太多卡片和封面会卡）.
    /// </summary>
    private const int PageBatchSize = 12;

    /// <summary>
    /// Initializes a new instance of the <see cref="MomentUperSectionViewModel"/> class.
    /// </summary>
    public MomentUperSectionViewModel(MomentProfile data)
        : base(data)
    {
        _service = this.Get<IMomentDiscoveryService>();
        _logger = this.Get<ILogger<MomentUperSectionViewModel>>();
        IsTotal = data is null;
        Avatar = data?.User?.Avatar.Uri;
        Name = data?.User?.Name ?? ResourceToolkit.GetLocalizedString(StringNames.AllDynamic);
        HasUnread = data?.IsUnread ?? false;
    }

    internal void InjectFirstPageData(MomentView momentView)
    {
        IsEmpty = false;
        _preventLoadMore = momentView.HasMoreMoments != true;
        _offset = momentView.Offset;
        _baseline = momentView.UpdateBaseline;
        this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _allItems.Clear();
            foreach (var item in momentView.Moments)
            {
                _allItems.Add(new MomentItemViewModel(item, MomentCardStyle.Comprehensive, ShowComment));
            }

            _visibleCount = PageBatchSize;
            ApplyVisibleWindow();
            ListUpdated?.Invoke(this, EventArgs.Empty);
        });
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (Items.Count > 0 || _preventLoadMore)
        {
            return;
        }

        await LoadItemsAsync();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (IsTotal)
        {
            throw new Exception("请在外层视图模型中刷新综合动态，以便更新 UP 列表");
        }

        _offset = default;
        _baseline = default;
        IsEmpty = false;
        _preventLoadMore = false;
        _visibleCount = PageBatchSize;
        _allItems.Clear();
        Items.Clear();

        this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await LoadItemsAsync();
        });
    }

    [RelayCommand]
    private async Task LoadItemsAsync()
    {
        if (IsEmpty || IsLoading || _preventLoadMore)
        {
            return;
        }

        try
        {
            IsLoading = true;
            if (IsTotal)
            {
                await LoadComprehensiveItemsAsync();
            }
            else
            {
                await LoadUperItemsAsync();
                if (HasUnread)
                {
                    HasUnread = false;
                }
            }
        }
        catch (Exception ex)
        {
            _preventLoadMore = true;
            _logger.LogError(ex, "加载动态列表失败.");
        }
        finally
        {
            IsEmpty = Items.Count == 0;
            IsLoading = false;
        }
    }

    private async Task LoadComprehensiveItemsAsync()
    {
        var view = await _service.GetComprehensiveMomentsAsync(_offset, _baseline);
        if (view is not null)
        {
            _offset = view.Offset;
            _baseline = view.UpdateBaseline;
            _preventLoadMore = view.HasMoreMoments != true;
            foreach (var item in view.Moments.Select(p => new MomentItemViewModel(p, MomentCardStyle.Comprehensive, ShowComment)))
            {
                _allItems.Add(item);
            }

            ApplyVisibleWindow();
            ListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadUperItemsAsync()
    {
        var (moments, offset, hasMore) = await _service.GetUserMomentsAsync(Data.User, _offset);
        _offset = offset;
        _preventLoadMore = !hasMore || string.IsNullOrEmpty(offset);
        if (moments is not null)
        {
            foreach (var item in moments.Select(p => new MomentItemViewModel(p, MomentCardStyle.Comprehensive, ShowComment)))
            {
                _allItems.Add(item);
            }

            ApplyVisibleWindow();
            ListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 已经加载、但还没铺到界面上的条目（等用户滚到底再补）.
    /// </summary>
    public bool HasHiddenItems => Items.Count < _allItems.Count;

    /// <summary>
    /// 再往界面补一批卡片（瀑布流滚到底时由界面调用）；没有更多就返回 <c>false</c>.
    /// </summary>
    /// <returns>是否真的补进来了新卡片.</returns>
    public bool LoadNextBatch()
    {
        if (Items.Count >= _allItems.Count)
        {
            return false;
        }

        _visibleCount = Math.Min(_allItems.Count, Items.Count + PageBatchSize);
        ApplyVisibleWindow();
        Serilog.Log.Information("动态哨兵（综合）：补到 {Shown}/{Total}", Items.Count, _allItems.Count);
        return true;
    }

    /// <summary>
    /// 把已加载条目里落在当前窗口内的那一段铺到界面上。
    /// 只往后追加，不动已有的卡片（<c>MomentWaterfall</c> 会按身份复用卡片，不会重建）.
    /// </summary>
    private void ApplyVisibleWindow()
    {
        var target = Math.Min(Math.Max(_visibleCount, 0), _allItems.Count);
        for (var i = Items.Count; i < target; i++)
        {
            Items.Add(_allItems[i]);
        }

        while (Items.Count > target)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    private void ShowComment(MomentItemViewModel item)
        => this.Get<MomentPageViewModel>().ShowCommentCommand.Execute(item.Data);
}
