// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel.Bili.Media;
using Richasy.BiliKernel.Models.Media;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 直播分区详情视图模型.
/// </summary>
public sealed partial class LivePartitionDetailViewModel : ViewModelBase<PartitionViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LivePartitionDetailViewModel"/> class.
    /// </summary>
    public LivePartitionDetailViewModel(
        PartitionViewModel partition,
        ILiveDiscoveryService service)
        : base(partition)
    {
        _service = service;
        _logger = this.Get<ILogger<LivePartitionDetailViewModel>>();
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (Children?.Count > 0)
        {
            return;
        }

        IsLiveLoading = true;
        try
        {
            var (lives, tags, nextPageNumber) = await _service.GetPartitionLiveListAsync(Data.Data);
            if (tags is null || tags.Count == 0)
            {
                _logger.LogWarning("{PartitionName} 分区未返回任何直播标签.", Data.Data.Name);
                return;
            }

            Children = [.. tags];
            TryAddRooms(lives);
            CurrentTag = tags[0];
            _childPartitionOffsetCache[CurrentTag] = nextPageNumber;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"尝试加载 {Data.Data.Name} 分区视频时出错.");
        }

        finally
        {
            IsLiveLoading = false;
            Initialized?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        if (CurrentTag is null)
        {
            return;
        }

        _childPartitionRoomCache.Remove(CurrentTag);
        _childPartitionOffsetCache.Remove(CurrentTag);
        _loadVersion++;
        var refreshVersion = _loadVersion;
        this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            if (refreshVersion != _loadVersion)
            {
                return;
            }

            Rooms.Clear();
            _preventLoadMore = false;
            await LoadRoomsAsync();
        });
    }

    [RelayCommand]
    private void ChangeChildPartition(LiveTag tag)
    {
        if (tag is null || tag.Equals(CurrentTag))
        {
            return;
        }

        _preventLoadMore = false;
        if (Rooms.Count > 0 && CurrentTag is not null)
        {
            _childPartitionRoomCache[CurrentTag] = Rooms.ToList();
        }

        _loadVersion++;
        CurrentTag = tag;
        var selectionVersion = _loadVersion;
        this.Get<DispatcherQueue>().TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            if (selectionVersion != _loadVersion || !Equals(tag, CurrentTag))
            {
                return;
            }

            Rooms.Clear();
            if (_childPartitionRoomCache.TryGetValue(tag, out var cache))
            {
                foreach (var item in cache)
                {
                    Rooms.Add(item);
                }
            }

            if (Rooms.Count == 0)
            {
                await LoadRoomsAsync();
            }
            else
            {
                LiveListUpdated?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    [RelayCommand]
    private async Task LoadRoomsAsync()
    {
        var requestedTag = CurrentTag;
        if (requestedTag is null || _preventLoadMore)
        {
            return;
        }

        var requestedVersion = _loadVersion;
        var hasNewRooms = false;
        _activeLoadCount++;
        IsLiveLoading = true;
        try
        {
            hasNewRooms = await LoadChildPartitionRoomsAsync(requestedTag, requestedVersion);
        }
        finally
        {
            _activeLoadCount--;
            IsLiveLoading = _activeLoadCount > 0;
        }

        if (hasNewRooms)
        {
            LiveListUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> LoadChildPartitionRoomsAsync(LiveTag tag, int loadVersion)
    {
        try
        {
            var pn = 0;
            if (_childPartitionOffsetCache.TryGetValue(tag, out var cachePN))
            {
                pn = cachePN;
            }

            var (rooms, _, nextPn) = await _service.GetPartitionLiveListAsync(Data.Data, tag, pn);
            if (loadVersion != _loadVersion || !Equals(tag, CurrentTag))
            {
                return false;
            }

            _childPartitionOffsetCache[tag] = nextPn;
            _preventLoadMore = rooms is null || rooms.Count == 0 || nextPn <= pn;
            TryAddRooms(rooms);
            return rooms?.Count > 0;
        }
        catch (Exception ex)
        {
            if (loadVersion == _loadVersion && Equals(tag, CurrentTag))
            {
                _preventLoadMore = true;
            }

            _logger.LogError(ex, "尝试加载 {PartitionName}/{TagName} 分区视频时出错.", Data.Data.Name, tag.Name ?? "<未命名>");
            return false;
        }
    }

    private void TryAddRooms(IReadOnlyList<LiveInformation> rooms)
    {
        if (rooms is not null)
        {
            foreach (var item in rooms)
            {
                Rooms.Add(new LiveItemViewModel(item, Models.Constants.LiveCardStyle.Partition));
            }
        }
    }
}
