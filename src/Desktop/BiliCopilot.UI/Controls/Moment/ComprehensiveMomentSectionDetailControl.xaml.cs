// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 综合动态分区详情控件.
/// 顶部为关注的 UP 主头像条，点击头像切换该 UP 主的动态.
/// </summary>
public sealed partial class ComprehensiveMomentSectionDetailControl : ComprehensiveSectionControlBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ComprehensiveMomentSectionDetailControl"/> class.
    /// </summary>
    public ComprehensiveMomentSectionDetailControl() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnControlLoaded()
    {
        if (ViewModel is not null)
        {
            ViewModel.Initialized += OnInitialized;
        }

        UperView.SelectionChanged += OnUperSelectionChanged;
        CheckSelection();
    }

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
    {
        UperView.ItemsSource = default;
        if (ViewModel is not null)
        {
            ViewModel.Initialized -= OnInitialized;
        }

        UperView.SelectionChanged -= OnUperSelectionChanged;
    }

    private void OnInitialized(object? sender, EventArgs e) => CheckSelection();

    private void OnUperSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is MomentUperSectionViewModel item)
        {
            ViewModel?.SelectUperCommand.Execute(item);
        }
    }

    private void CheckSelection()
    {
        if (ViewModel?.SelectedUper is not null)
        {
            var index = ViewModel.Upers.IndexOf(ViewModel.SelectedUper);
            if (index >= 0)
            {
                UperView.Select(index);
            }
        }
    }
}
