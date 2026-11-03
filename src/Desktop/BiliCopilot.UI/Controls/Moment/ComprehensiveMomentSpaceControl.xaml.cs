// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 综合动态空间控件.
/// 按窗口宽度在 1/2/3 列之间切换，形成瀑布流布局.
/// </summary>
public sealed partial class ComprehensiveMomentSpaceControl : UserMomentDetailControlBase
{
    private const double TwoColumnThreshold = 980d;
    private const double ThreeColumnThreshold = 1500d;

    private readonly ObservableCollection<MomentItemViewModel> _firstColumn = new();
    private readonly ObservableCollection<MomentItemViewModel> _secondColumn = new();
    private readonly ObservableCollection<MomentItemViewModel> _thirdColumn = new();
    private int _columnCount = 1;
    private bool _isRedistributing;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComprehensiveMomentSpaceControl"/> class.
    /// </summary>
    public ComprehensiveMomentSpaceControl()
    {
        InitializeComponent();
        FirstRepeater.ItemsSource = _firstColumn;
        SecondRepeater.ItemsSource = _secondColumn;
        ThirdRepeater.ItemsSource = _thirdColumn;
    }

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        ViewModel.ListUpdated += OnListUpdatedAsync;
        ViewModel.Items.CollectionChanged += OnItemsCollectionChanged;
        ApplyColumnCount(GetColumnCountForWidth(ActualWidth), true);
    }

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        if (ViewModel is not null)
        {
            ViewModel.ListUpdated -= OnListUpdatedAsync;
            ViewModel.Items.CollectionChanged -= OnItemsCollectionChanged;
        }
    }

    private static int GetColumnCountForWidth(double width)
    {
        if (width >= ThreeColumnThreshold)
        {
            return 3;
        }

        return width >= TwoColumnThreshold ? 2 : 1;
    }

    private void OnMomentGridSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyColumnCount(GetColumnCountForWidth(e.NewSize.Width));

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Redistribute();

    private void ApplyColumnCount(int count, bool force = false)
    {
        if (!force && count == _columnCount)
        {
            return;
        }

        _columnCount = count;
        ColumnTwo.Width = count >= 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColumnThree.Width = count >= 3 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        MomentGrid.ColumnSpacing = count > 1 ? 12 : 0;
        Redistribute();
    }

    private void Redistribute()
    {
        if (ViewModel is null || _isRedistributing)
        {
            return;
        }

        try
        {
            _isRedistributing = true;
            _firstColumn.Clear();
            _secondColumn.Clear();
            _thirdColumn.Clear();
            var index = 0;
            foreach (var item in ViewModel.Items)
            {
                switch (index % _columnCount)
                {
                    case 0:
                        _firstColumn.Add(item);
                        break;
                    case 1:
                        _secondColumn.Add(item);
                        break;
                    default:
                        _thirdColumn.Add(item);
                        break;
                }

                index++;
            }
        }
        finally
        {
            _isRedistributing = false;
        }
    }

    private async void OnListUpdatedAsync(object? sender, EventArgs e)
    {
        await View.DelayCheckItemsAsync();
    }
}
