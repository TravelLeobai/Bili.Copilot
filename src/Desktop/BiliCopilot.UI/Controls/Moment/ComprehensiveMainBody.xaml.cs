// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Reactor;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Reactor.Hosting;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 综合动态分区主体.
/// 列表部分由 Reactor 的瀑布流组件渲染.
/// </summary>
public sealed partial class ComprehensiveMainBody : MomentUperSectionControlBase
{
    private ReactorHostControl? _reactorHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComprehensiveMainBody"/> class.
    /// </summary>
    public ComprehensiveMainBody() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnControlLoaded()
        => MountWaterfall(ViewModel);

    /// <inheritdoc/>
    protected override void OnControlUnloaded()
        => MountWaterfall(null);

    /// <inheritdoc/>
    protected override void OnViewModelChanged(MomentUperSectionViewModel? oldValue, MomentUperSectionViewModel? newValue)
        => MountWaterfall(newValue);

    private void MountWaterfall(MomentUperSectionViewModel? viewModel)
    {
        _reactorHost?.Dispose();
        _reactorHost = null;
        RootGrid.Children.Clear();

        if (viewModel is null)
        {
            return;
        }

        _reactorHost = new ReactorHostControl();
        RootGrid.Children.Add(_reactorHost);
        _reactorHost.Mount(new MomentWaterfall(viewModel));
    }
}
