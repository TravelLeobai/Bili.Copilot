// Copyright (c) Bili Copilot. All rights reserved.

using System.ComponentModel;
using BiliCopilot.UI.ViewModels.Items;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BiliCopilot.UI.Controls.Comment;

/// <summary>
/// 评论附带的投票：标题、选项与投票按钮，或者结果。
/// <para>
/// 面板在弹窗里，关掉就会卸载。卸载时把订阅和监听都放掉、在途的详情请求作废，
/// 免得回包往一个已经没了的控件上写.
/// </para>
/// </summary>
public sealed partial class CommentVotePanel : UserControl
{
    /// <summary>
    /// <see cref="ViewModel"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(CommentVoteViewModel), typeof(CommentVotePanel), new PropertyMetadata(default(CommentVoteViewModel), OnViewModelChanged));

    private CommentVoteViewModel? _observed;
    private FocusState _submitFocus;
    private bool _isLoaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentVotePanel"/> class.
    /// </summary>
    public CommentVotePanel()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Gets or sets 要展示的投票.
    /// </summary>
    public CommentVoteViewModel? ViewModel
    {
        get => (CommentVoteViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (CommentVotePanel)d;
        panel.Observe(panel._isLoaded ? e.NewValue as CommentVoteViewModel : null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ViewModel?.Reattach();
        Observe(ViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        Observe(null);
        ViewModel?.Detach();
    }

    private void Observe(CommentVoteViewModel? viewModel)
    {
        if (!ReferenceEquals(_observed, viewModel))
        {
            if (_observed is not null)
            {
                _observed.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _observed = viewModel;
            if (_observed is not null)
            {
                _observed.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        UpdateOptionsFocus();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CommentVoteViewModel.Status):
                // 正在提交、投成了、没投成、还能选几项：这些都值得念出来.
                if (_observed is { HasStatus: true })
                {
                    Announce(StatusBlock);
                }

                break;
            case nameof(CommentVoteViewModel.Phase):
                if (_observed is { IsFailed: true })
                {
                    Announce(FailedBlock);
                }
                else if (_observed is { IsUnavailable: true })
                {
                    Announce(UnavailableBlock);
                }

                break;
            case nameof(CommentVoteViewModel.IsChoosing):
            case nameof(CommentVoteViewModel.IsSubmitting):
                UpdateOptionsFocus();
                break;
            default:
                break;
        }
    }

    // 选项行自己不能拿焦点时（正在提交、或者已经在看结果），让列表接管，
    // 这样结果用键盘也够得着，投完之后焦点也有地方可待.
    private void UpdateOptionsFocus()
    {
        var rowsTakeFocus = _observed is { IsChoosing: true, IsSubmitting: false };
        OptionsScroller.IsTabStop = !rowsTakeFocus;
        if (_submitFocus == FocusState.Unfocused)
        {
            return;
        }

        if (_observed is { IsSubmitting: true })
        {
            OptionsScroller.Focus(_submitFocus);
            return;
        }

        var focus = _submitFocus;
        _submitFocus = FocusState.Unfocused;
        if (rowsTakeFocus)
        {
            // 没投成：把焦点送回按钮，再按回车就是重试.
            // 排队执行：按钮要等这次通知走完之后才会重新可用.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_observed is { IsChoosing: true, IsSubmitting: false } && SubmitButton.IsEnabled)
                {
                    SubmitButton.Focus(focus);
                }
            });
        }
    }

    // 排队：文字和它的可见性绑在还在通知的属性上.
    private void Announce(TextBlock text)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (text.Visibility == Visibility.Visible)
            {
                FrameworkElementAutomationPeer.CreatePeerForElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        });

    private void OnSubmitClick(object sender, RoutedEventArgs e)
        => _submitFocus = SubmitButton.FocusState == FocusState.Unfocused ? FocusState.Programmatic : SubmitButton.FocusState;

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: CommentVoteOptionViewModel option } button)
        {
            return;
        }

        ViewModel?.ToggleOption(option);

        // ToggleButton 自己已经翻过来了；这里按投票真正接受的结果改回去.
        button.IsChecked = option.IsChecked;
    }
}
