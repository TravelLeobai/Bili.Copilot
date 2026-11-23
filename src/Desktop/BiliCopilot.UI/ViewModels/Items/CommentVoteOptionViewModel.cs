// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Toolkits;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using Microsoft.UI.Xaml.Media;
using Symbol = FluentIcons.Common.Symbol;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 投票的一个选项：可投的时候是个可选项，投完之后是结果行.
/// </summary>
public sealed partial class CommentVoteOptionViewModel : ObservableObject
{
    private readonly bool _isMultiChoice;

    internal CommentVoteOptionViewModel(Services.CommentVoteOption option, bool isMultiChoice, int order = 0)
    {
        _isMultiChoice = isMultiChoice;
        Index = option.Index;
        Order = order;
        Text = option.Text;
        Image = option.Image;
        Count = option.Count;
        Stats = string.Empty;
        ResultName = string.Empty;
    }

    /// <summary>
    /// Gets 服务端认的选项序号.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets 选项在列表里的位置（0 起），用来取饼图配色.
    /// </summary>
    public int Order { get; }

    /// <summary>
    /// Gets 饼图里这一片和图例小圆点的颜色.
    /// </summary>
    public Brush Swatch => CommentVotePalette.GetBrush(Order);

    /// <summary>
    /// Gets 选项正文.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets 图片投票的配图；没有就是 null.
    /// </summary>
    public Uri? Image { get; }

    /// <summary>
    /// Gets a value indicating whether 这个选项有配图.
    /// </summary>
    public bool HasImage => Image is not null;

    /// <summary>
    /// Gets 选项前面的记号：多选是方框，单选是圆点.
    /// </summary>
    public Symbol Glyph => _isMultiChoice
        ? (IsChecked ? Symbol.CheckboxChecked : Symbol.CheckboxUnchecked)
        : (IsChecked ? Symbol.RadioButton : Symbol.Circle);

    /// <summary>
    /// Gets 记号的填充方式：选中后填实.
    /// </summary>
    public IconVariant GlyphVariant => IsChecked ? IconVariant.Filled : IconVariant.Regular;

    /// <summary>
    /// Gets a value indicating whether 这一行显示的是结果（不是可选项）.
    /// </summary>
    public bool IsResult => !IsChoice;

    /// <summary>
    /// Gets or sets a value indicating whether 这一行是可选项.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResult))]
    private bool _isChoice;

    /// <summary>
    /// Gets or sets a value indicating whether 现在能不能勾选或取消它.
    /// </summary>
    [ObservableProperty]
    private bool _canChoose;

    /// <summary>
    /// Gets or sets a value indicating whether 用户已经把它勾上了（还没提交）.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(GlyphVariant))]
    private bool _isChecked;

    /// <summary>
    /// Gets or sets a value indicating whether 这是登录用户投过的那一项.
    /// </summary>
    [ObservableProperty]
    private bool _isMine;

    /// <summary>
    /// Gets or sets 票数.
    /// </summary>
    [ObservableProperty]
    private long _count;

    /// <summary>
    /// Gets or sets 占总票数的百分比（0-100）.
    /// </summary>
    [ObservableProperty]
    private double _percent;

    /// <summary>
    /// Gets or sets 票数与占比，直接显示.
    /// </summary>
    [ObservableProperty]
    private string _stats;

    /// <summary>
    /// Gets or sets 读屏念这一行的结果时说的话.
    /// </summary>
    [ObservableProperty]
    private string _resultName;
}
