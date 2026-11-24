// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Specialized;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace BiliCopilot.UI.Controls.Player;

/// <summary>
/// 直播弹幕浮层：分轨道自右向左滚动.
/// </summary>
public sealed partial class LiveDanmakuOverlay : UserControl
{
    private const double LaneHeight = 30;
    private const double Speed = 170;

    /// <summary>
    /// 同屏上限（原来按 <c>RootCanvas.Children.Count &gt; 60</c> 压旧弹幕，这里按活动弹幕数计，口径一致）.
    /// </summary>
    private const int MaxConcurrent = 60;

    // 槽位池：每条弹幕要用的一套 XAML 对象（TextBlock / TranslateTransform / DoubleAnimation / Storyboard）
    // 都是无状态复用得起来的，跑完回收，下一条接着用；同屏最多 60 条，所以池子一辈子也就 60 出头个槽位.
    private readonly Stack<DanmakuSlot> _freeSlots = new();

    // 按上屏顺序排列的活动槽位：压旧弹幕时从表首取（原实现是 Children[0]）.
    private readonly LinkedList<DanmakuSlot> _activeSlots = new();

    // 白色画笔全屏共用一份：颜色固定、无状态，60 条弹幕没必要各持一份.
    private SolidColorBrush? _sharedBrush;
    private PlayerViewModel? _viewModel;
    private int _laneIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveDanmakuOverlay"/> class.
    /// </summary>
    public LiveDanmakuOverlay()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 播放器视图模型.
    /// </summary>
    public PlayerViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value))
            {
                return;
            }

            if (_viewModel is not null)
            {
                _viewModel.LiveInteractions.CollectionChanged -= OnInteractionsChanged;
            }

            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.LiveInteractions.CollectionChanged += OnInteractionsChanged;
            }
        }
    }

    private void OnInteractionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        foreach (var item in e.NewItems)
        {
            if (item is LiveInteractionViewModel { Kind: LiveInteractionKind.Danmaku } danmaku)
            {
                AddDanmaku(danmaku.Text);
            }
        }
    }

    private void AddDanmaku(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // 每条弹幕不再 new 一整套对象：从池里取一个槽位，只改属性.
        var slot = RentSlot();
        var block = slot.Block;
        var width = ActualWidth;

        block.Text = text;
        block.MaxWidth = Math.Max(200, width * 0.7);
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var blockWidth = block.DesiredSize.Width;
        var laneCount = Math.Max(1, (int)(ActualHeight / LaneHeight));
        var lane = _laneIndex++ % laneCount;
        Canvas.SetTop(block, lane * LaneHeight + 8);

        // 动画参数逐条不同（起点、终点、时长），槽位可以复用，这几个数必须每条重设.
        slot.Transform.X = width;
        slot.Animation.From = width;
        slot.Animation.To = -blockWidth;
        slot.Animation.Duration = new Duration(TimeSpan.FromSeconds((width + blockWidth) / Speed));

        slot.IsActive = true;
        _activeSlots.AddLast(slot.Node);
        RootCanvas.Children.Add(block);
        slot.Storyboard.Begin();

        // 控制同屏数量，避免长时间挂机后元素堆积；与原来一样从最旧的一条开始压掉.
        while (_activeSlots.Count > MaxConcurrent)
        {
            ReleaseSlot(_activeSlots.First!.Value);
        }
    }

    private DanmakuSlot RentSlot()
    {
        if (_freeSlots.Count > 0)
        {
            return _freeSlots.Pop();
        }

        _sharedBrush ??= new SolidColorBrush(Colors.White);
        return new DanmakuSlot(this, _sharedBrush);
    }

    /// <summary>
    /// 回收一个槽位：停动画、离场、回池.
    /// 两种情况会走到这里——动画自然跑完（Storyboard.Completed），或同屏超限被新弹幕顶掉.
    /// </summary>
    /// <param name="slot">槽位.</param>
    private void ReleaseSlot(DanmakuSlot slot)
    {
        if (!slot.IsActive)
        {
            return;
        }

        slot.IsActive = false;

        // Stop() 之后 Completed 不会再触发，所以被顶掉的槽位不会二次回收.
        slot.Storyboard.Stop();
        _activeSlots.Remove(slot.Node);
        RootCanvas.Children.Remove(slot.Block);
        _freeSlots.Push(slot);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LiveInteractions.CollectionChanged -= OnInteractionsChanged;
        }
    }

    /// <summary>
    /// 一条弹幕占用的那套 XAML 对象。
    /// 建一次、用一辈子：弹幕文本与动画参数每次重设，对象本身不再重建.
    /// </summary>
    private sealed class DanmakuSlot
    {
        public DanmakuSlot(LiveDanmakuOverlay owner, SolidColorBrush brush)
        {
            Transform = new TranslateTransform();
            Block = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush,
                IsHitTestVisible = false,
                Shadow = new ThemeShadow(),
                RenderTransform = Transform,
            };

            Animation = new DoubleAnimation { EnableDependentAnimation = true };
            Storyboard = new Storyboard();
            Storyboard.Children.Add(Animation);
            Storyboard.SetTarget(Animation, Transform);
            Storyboard.SetTargetProperty(Animation, "X");

            // 回调只挂一次：原来每条弹幕都要捕获一个新闭包（连同 block、storyboard 一起拖住）.
            Storyboard.Completed += (_, _) => owner.ReleaseSlot(this);

            // 链表节点也预建，避免每条弹幕 AddLast 时再分配一个节点.
            Node = new LinkedListNode<DanmakuSlot>(this);
        }

        public TextBlock Block { get; }

        public TranslateTransform Transform { get; }

        public DoubleAnimation Animation { get; }

        public Storyboard Storyboard { get; }

        public LinkedListNode<DanmakuSlot> Node { get; }

        public bool IsActive { get; set; }
    }
}
