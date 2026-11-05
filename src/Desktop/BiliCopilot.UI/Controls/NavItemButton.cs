// Copyright (c) Bili Copilot. All rights reserved.

using System.Numerics;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.Controls;

/// <summary>
/// 侧栏导航项：左边一个图标、右边文字的横向版式。
/// 图标由 Regular/Filled 两个 SymbolIcon 叠在一起，选中时靠 200ms 透明度动画交叉淡入.
/// 这里不继承 Button（避免 WinRT 类型导出限制），对外只暴露内部的 Button.
/// </summary>
public sealed class NavItemButton
{
    private readonly Button _button;
    private readonly FluentIcons.WinUI.SymbolIcon _regularIcon;
    private readonly FluentIcons.WinUI.SymbolIcon _filledIcon;
    private readonly TextBlock _label;
    private readonly Border _backplate;
    private readonly Border _pill;

    private FluentIcons.Common.Symbol _symbol;
    private bool _isItemSelected;

    /// <summary>
    /// Initializes a new instance of the <see cref="NavItemButton"/> class.
    /// </summary>
    public NavItemButton()
    {
        _regularIcon = new FluentIcons.WinUI.SymbolIcon
        {
            IconVariant = FluentIcons.Common.IconVariant.Regular,
            Opacity = 1,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _filledIcon = new FluentIcons.WinUI.SymbolIcon
        {
            IconVariant = FluentIcons.Common.IconVariant.Filled,
            Opacity = 0,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // 交叉淡入就是这两条隐式动画在跑，不另写 storyboard.
        var animations = new ImplicitAnimationSet
        {
            new OpacityAnimation { Duration = TimeSpan.FromMilliseconds(200) },
        };
        Implicit.SetAnimations(_regularIcon, animations);
        Implicit.SetAnimations(_filledIcon, animations);

        var iconHost = new Grid { Width = 40, Height = 40 };
        iconHost.Children.Add(_regularIcon);
        iconHost.Children.Add(_filledIcon);

        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(2, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // 选中时左侧这根胶囊，参照 WinUI 设置页的选中指示器，带一点弹性.
        _pill = new Border
        {
            Width = 4,
            Height = 24,
            CornerRadius = new CornerRadius(2),
            Background = ResolveBrush("AccentFillColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var root = new Grid { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_pill);
        Grid.SetColumn(iconHost, 1);
        root.Children.Add(iconHost);
        Grid.SetColumn(_label, 2);
        root.Children.Add(_label);

        _backplate = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(6),
            Child = root,
        };

        _button = new Button
        {
            Content = _backplate,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 1, 4, 1),
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };

        _button.PointerEntered += (_, _) => ApplyVisualState();
        _button.PointerExited += (_, _) => ApplyVisualState();
        _button.PointerPressed += (_, _) => ApplyVisualState();
        _button.PointerReleased += (_, _) => ApplyVisualState();
    }

    /// <summary>
    /// 真正放进可视树的按钮.
    /// </summary>
    public Button Root => _button;

    /// <summary>
    /// 要显示的图标.
    /// </summary>
    public FluentIcons.Common.Symbol Symbol
    {
        get => _symbol;
        set
        {
            _symbol = value;
            _regularIcon.Symbol = value;
            _filledIcon.Symbol = value;
        }
    }

    /// <summary>
    /// 是否被选中；变化时触发图标交叉淡入.
    /// </summary>
    public bool IsItemSelected
    {
        get => _isItemSelected;
        set
        {
            if (_isItemSelected == value)
            {
                return;
            }

            _isItemSelected = value;
            ApplyVisualState();

            if (value)
            {
                PlayPillAnimation();
            }
        }
    }

    /// <summary>
    /// 文字，侧栏折叠时传空串.
    /// </summary>
    public string Label
    {
        get => _label.Text;
        set
        {
            _label.Text = value ?? string.Empty;
            _label.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void PlayPillAnimation()
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(_pill);
            visual.CenterPoint = new Vector3((float)_pill.Width / 2f, (float)_pill.Height / 2f, 0f);
            var spring = visual.Compositor.CreateSpringVector3Animation();
            spring.InitialValue = new Vector3(1f, 0.3f, 1f);
            spring.FinalValue = Vector3.One;
            spring.DampingRatio = 0.55f;
            spring.Period = TimeSpan.FromMilliseconds(90);
            visual.StartAnimation("Scale", spring);
        }
        catch
        {
            // 动画失败不影响选中态.
        }
    }

    private void ApplyVisualState()
    {
        var hovered = _button.IsPointerOver;
        var pressed = _button.IsPressed;
        var accent = ResolveBrush("AccentTextFillColorPrimaryBrush");
        var normal = ResolveBrush("TextFillColorPrimaryBrush");
        var iconColor = _isItemSelected ? accent : normal;

        _regularIcon.Opacity = _isItemSelected ? 0 : 1;
        _filledIcon.Opacity = _isItemSelected ? 1 : 0;
        _regularIcon.Foreground = iconColor;
        _filledIcon.Foreground = iconColor;
        _label.Foreground = _isItemSelected ? accent : normal;
        _pill.Visibility = _isItemSelected ? Visibility.Visible : Visibility.Collapsed;

        _backplate.Background = _isItemSelected
            ? ResolveBrush("ControlFillColorSecondaryBrush")
            : pressed
                ? ResolveBrush("SubtleFillColorTertiaryBrush")
                : hovered
                    ? ResolveBrush("SubtleFillColorSecondaryBrush")
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private static Brush ResolveBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
