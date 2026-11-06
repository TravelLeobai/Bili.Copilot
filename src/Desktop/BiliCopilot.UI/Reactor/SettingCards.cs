// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace BiliCopilot.UI.Reactor;

/// <summary>
/// 设置卡的 Reactor 版本：左边图标 + 标题 + 描述，右边放具体控件.
/// 控件本体还是原生的（ToggleSwitch / ComboBox 之类），用 XamlHostElement 挂进来，
/// 这样事件和绑定行为跟原来的 XAML 卡片一致.
/// </summary>
internal static class SettingCards
{
    // 图标控件复用，避免每次 Render 新建一堆 SymbolIcon.
    private static readonly Dictionary<FluentIcons.Common.Symbol, FluentIcons.WinUI.SymbolIcon> IconCache = new();

    /// <summary>
    /// 一张设置卡.
    /// </summary>
    /// <param name="header">标题.</param>
    /// <param name="description">描述，可为空.</param>
    /// <param name="icon">左侧图标，没有就传 null.</param>
    /// <param name="content">右侧内容控件.</param>
    public static Element Card(string header, string? description, FluentIcons.Common.Symbol? icon, Element content)
    {
        var iconPart = icon is { } symbol
            ? new XamlHostElement(() => GetIcon(symbol), _ => { })
                .Size(20, 20)
                .VAlign(VerticalAlignment.Center)
            : (Element)TextBlock(string.Empty);

        var texts = new List<Element> { TextBlock(header).FontSize(14) };
        if (!string.IsNullOrEmpty(description))
        {
            texts.Add(TextBlock(description!).FontSize(12).Foreground(LookupBrush("TextFillColorSecondaryBrush")));
        }

        return Grid(
            columns: [GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            HStack(14, iconPart, VStack(2, [.. texts]).VAlign(VerticalAlignment.Center))
                .Grid(row: 0, column: 0)
                .Padding(16, 10, 16, 10)
                .VAlign(VerticalAlignment.Center),
            content
                .Grid(row: 0, column: 1)
                .Padding(0, 10, 16, 10)
                .VAlign(VerticalAlignment.Center))
            .Background(LookupBrush("CardBackgroundFillColorDefaultBrush"))
            .CornerRadius(4)
            .Margin(0, 1, 0, 1);
    }

    /// <summary>
    /// 一组卡片：外面再包一层上下间距，对应原来 StackPanel Spacing=4.
    /// </summary>
    public static Element Group(params Element[] cards)
        => VStack(2, cards);

    private static FluentIcons.WinUI.SymbolIcon GetIcon(FluentIcons.Common.Symbol symbol)
    {
        if (!IconCache.TryGetValue(symbol, out var icon))
        {
            icon = new FluentIcons.WinUI.SymbolIcon { Symbol = symbol, FontSize = 18 };
            IconCache[symbol] = icon;
        }

        return icon;
    }

    private static Brush? LookupBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : null;

    /// <summary>
    /// 把一个原生控件包成 Reactor 元素，尺寸交给调用方控制.
    /// </summary>
    public static Element Host(FrameworkElement element)
        => new XamlHostElement(() => element, _ => { });

    /// <summary>
    /// 双向绑定的开关：原生 ToggleSwitch 和 view model 属性互相同步.
    /// </summary>
    public static (Element Element, ToggleSwitch Control) Toggle(bool initial, Action<bool> onChanged, string? automationName = null)
    {
        var toggle = new ToggleSwitch { IsOn = initial, MinWidth = 0 };
        if (!string.IsNullOrEmpty(automationName))
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, automationName);
        }

        toggle.Toggled += (_, _) => onChanged(toggle.IsOn);
        return (new XamlHostElement(() => toggle, _ => { }), toggle);
    }

    /// <summary>
    /// 动作按钮.
    /// </summary>
    public static Element ActionButton(string text, Action onClick, bool isEnabled = true)
    {
        var button = new Button { Content = text, MinWidth = 120, IsEnabled = isEnabled };
        button.Click += (_, _) => onClick();
        return new XamlHostElement(() => button, _ => { });
    }
}
