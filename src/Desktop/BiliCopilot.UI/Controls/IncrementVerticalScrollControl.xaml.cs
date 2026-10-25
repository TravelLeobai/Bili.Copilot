using Richasy.WinUIKernel.Share.Base;
using System.Windows.Input;

namespace BiliCopilot.UI.Controls;

public sealed partial class IncrementVerticalScrollControl : LayoutUserControlBase
{
    private bool _loadCheckQueued;

    /// <summary>
    /// 临时暂停自动加载，避免调整分栏时发起新请求.
    /// </summary>
    public bool IsLoadMorePaused { get; set; }

    public static readonly DependencyProperty ElementProperty =
        DependencyProperty.Register(nameof(Element), typeof(object), typeof(IncrementVerticalScrollControl), new PropertyMetadata(default));

    public static readonly DependencyProperty LoadMoreCommandProperty =
        DependencyProperty.Register(nameof(LoadMoreCommand), typeof(ICommand), typeof(IncrementVerticalScrollControl), new PropertyMetadata(default));

    public IncrementVerticalScrollControl() => InitializeComponent();

    /// <summary>
    /// 滚动位置已变化，供时间线等关联视图同步位置.
    /// </summary>
    public event EventHandler? ScrollPositionChanged;

    public object Element
    {
        get => (object)GetValue(ElementProperty);
        set => SetValue(ElementProperty, value);
    }

    public ICommand LoadMoreCommand
    {
        get => (ICommand)GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public async Task DelayCheckItemsAsync()
    {
        await Task.Delay(500);
        CheckItemsCount();
    }

    public void ResetScrollPosition()
    {
        if (View is null)
        {
            return;
        }

        View.ScrollTo(0, 0);
    }

    public double GetScrollPosition()
    {
        if (View is null)
        {
            return 0;
        }

        return View.VerticalOffset;
    }

    protected override void OnControlLoaded()
    {
        View.ViewChanged += OnViewChanged;
        View.SizeChanged += OnViewSizeChanged;
    }

    protected override void OnControlUnloaded()
    {
        View.ViewChanged -= OnViewChanged;
        View.SizeChanged -= OnViewSizeChanged;
    }

    private void OnViewChanged(ScrollView sender, object e)
    {
        CheckItemsCount();
        ScrollPositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 100)
        {
            CheckItemsCount();
        }
    }

    private void CheckItemsCount()
    {
        if (IsLoadMorePaused || _loadCheckQueued)
        {
            return;
        }

        _loadCheckQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _loadCheckQueued = false;
            if (!IsLoadMorePaused && IsLoaded && View.ExtentHeight - View.ViewportHeight - View.VerticalOffset <= 800)
            {
                LoadMoreCommand?.Execute(default);
            }
        });
    }
}
