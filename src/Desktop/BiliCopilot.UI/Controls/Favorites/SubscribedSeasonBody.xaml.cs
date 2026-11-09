// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;

namespace BiliCopilot.UI.Controls.Favorites;

/// <summary>
/// 订阅合集分区主体.
/// </summary>
public sealed partial class SubscribedSeasonBody : SubscribedSeasonControlBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubscribedSeasonBody"/> class.
    /// </summary>
    public SubscribedSeasonBody() => InitializeComponent();

    private void OnSeasonCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => Open(sender, "tapped");

    private void OnSeasonCardPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => Open(sender, "pointer-released");

    private void Open(object sender, string source)
    {
        var item = Resolve(sender);
        Services.StartupTrace.Mark("SeasonCard:" + source, item?.Title ?? "no-vm");
        item?.OpenCommand.Execute(default);
    }

    private static SeasonSubscriptionItemViewModel? Resolve(object sender)
    {
        if (sender is FrameworkElement element)
        {
            if (element.Tag is SeasonSubscriptionItemViewModel tagged)
            {
                return tagged;
            }

            if (element.DataContext is SeasonSubscriptionItemViewModel fromContext)
            {
                return fromContext;
            }
        }

        return null;
    }
}

