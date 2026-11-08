// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Items;
using System.Collections.ObjectModel;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 直播分区的 UP 主过滤栏（XAML 部分，供 Reactor 宿主嵌入）.
/// </summary>
public sealed partial class LiveUperBar : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveUperBar"/> class.
    /// </summary>
    public LiveUperBar()
    {
        InitializeComponent();
        UperView.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>
    /// 绑定视图模型.
    /// </summary>
    /// <param name="viewModel">直播分区视图模型.</param>
    public void Bind(LiveMomentSectionDetailViewModel viewModel)
    {
        _viewModel = viewModel;
        UperView.ItemsSource = viewModel.Upers;
        SyncSelection();
        viewModel.Upers.CollectionChanged -= OnUpersChanged;
        viewModel.Upers.CollectionChanged += OnUpersChanged;
    }

    private LiveMomentSectionDetailViewModel? _viewModel;

    private void OnUpersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => SyncSelection();

    private void SyncSelection()
    {
        // 过滤项重建后旧选中项已经失效，先清掉选中；
        // 默认的"全部"由 ViewModel 自己选，界面不强调选中态.
        UperView.DeselectAll();
    }

    private void OnSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is LiveSectionUperViewModel uper && _viewModel is not null)
        {
            _viewModel.SelectUper(uper);
        }
    }
}


