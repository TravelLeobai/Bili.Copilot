// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Message;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 使用 Reactor 重写的消息页面外壳.
/// 布局与分栏由 Reactor 渲染，列表与聊天内容复用原有控件.
/// </summary>
public sealed class MessageShell : Component
{
    private readonly MessagePageViewModel _viewModel;
    private readonly MessagePageSideHeader _sideHeader = new();
    private readonly MessagePageSideBody _sideBody = new();
    private readonly MessagePageMainHeader _mainHeader = new();
    private readonly MessagePageMainBody _mainBody = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageShell"/> class.
    /// </summary>
    public MessageShell(MessagePageViewModel viewModel) => _viewModel = viewModel;

    /// <inheritdoc/>
    public override Element Render()
    {
        UseObservableTree(_viewModel);

        var side = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            new XamlHostElement(() => _sideHeader, _ => { }).Grid(row: 0),
            new XamlHostElement(() => _sideBody, _ => { }).Grid(row: 1));

        var main = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            new XamlHostElement(() => _mainHeader, _ => { }).Grid(row: 0),
            new XamlHostElement(() => _mainBody, _ => { }).Grid(row: 1));

        var root = Grid(
            columns: [GridSize.Px(296), GridSize.Star()],
            rows: [GridSize.Star()],
            side.Grid(row: 0, column: 0),
            main.Grid(row: 0, column: 1));

        return Border(root).Margin(0);
    }
}
