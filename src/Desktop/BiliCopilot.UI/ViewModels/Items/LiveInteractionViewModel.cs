// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.Live;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 直播间互动消息条目（弹幕 / 礼物 / 醒目留言 / 上舰 / 进场）.
/// </summary>
public sealed partial class LiveInteractionViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveInteractionViewModel"/> class.
    /// </summary>
    public LiveInteractionViewModel(LiveInteractionItem item)
    {
        Kind = item.Kind;
        UserName = string.IsNullOrEmpty(item.UserName) ? "匿名" : item.UserName;
        Text = item.Text;
        UserLevel = item.UserLevel;
        MedalLevel = item.MedalLevel;
        MedalName = item.MedalName;
        GiftName = item.GiftName;
        GiftCount = item.GiftCount;
        GiftPrice = item.GiftPrice;
        GuardLevel = item.GuardLevel;
        Avatar = item.Avatar;
        BackgroundColor = item.BackgroundColor;
        Time = item.Time;
    }

    /// <summary>
    /// 消息类型.
    /// </summary>
    public LiveInteractionKind Kind { get; }

    /// <summary>
    /// 用户名.
    /// </summary>
    public string UserName { get; }

    /// <summary>
    /// 正文.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// 用户等级.
    /// </summary>
    public int UserLevel { get; }

    /// <summary>
    /// 粉丝勋章等级.
    /// </summary>
    public int MedalLevel { get; }

    /// <summary>
    /// 粉丝勋章名称.
    /// </summary>
    public string MedalName { get; }

    /// <summary>
    /// 礼物名.
    /// </summary>
    public string GiftName { get; }

    /// <summary>
    /// 礼物数量.
    /// </summary>
    public int GiftCount { get; }

    /// <summary>
    /// 礼物 / 醒目留言价格（元）.
    /// </summary>
    public int GiftPrice { get; }

    /// <summary>
    /// 舰长等级（1 舰长 / 2 提督 / 3 总督）.
    /// </summary>
    public int GuardLevel { get; }

    /// <summary>
    /// 头像地址.
    /// </summary>
    public string Avatar { get; }

    /// <summary>
    /// 醒目留言背景色.
    /// </summary>
    public string BackgroundColor { get; }

    /// <summary>
    /// 收到时间.
    /// </summary>
    public DateTimeOffset Time { get; }

    /// <summary>
    /// 是否显示用户等级徽章.
    /// </summary>
    public bool HasUserLevel => UserLevel > 0;

    /// <summary>
    /// 是否显示粉丝勋章.
    /// </summary>
    public bool HasMedal => MedalLevel > 0 && !string.IsNullOrEmpty(MedalName);

    /// <summary>
    /// 是否礼物类消息.
    /// </summary>
    public bool IsGift => Kind == LiveInteractionKind.Gift;

    /// <summary>
    /// 是否醒目留言.
    /// </summary>
    public bool IsSuperChat => Kind == LiveInteractionKind.SuperChat;

    /// <summary>
    /// 是否上舰.
    /// </summary>
    public bool IsGuardBuy => Kind == LiveInteractionKind.Guard;

    /// <summary>
    /// 是否进场类消息.
    /// </summary>
    public bool IsEnter => Kind == LiveInteractionKind.Enter;

    /// <summary>
    /// 用户等级文本.
    /// </summary>
    public string UserLevelText => $"UL{UserLevel}";

    /// <summary>
    /// 勋章文本.
    /// </summary>
    public string MedalText => $"{MedalName} {MedalLevel}";

    /// <summary>
    /// 礼物描述.
    /// </summary>
    public string GiftDescription => string.IsNullOrEmpty(GiftName)
        ? string.Empty
        : $"{GiftName} × {GiftCount}";

    /// <summary>
    /// 价格文本.
    /// </summary>
    public string PriceText => GiftPrice > 0 ? $"¥{GiftPrice}" : string.Empty;

    /// <summary>
    /// 时间文本.
    /// </summary>
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>
    /// 醒目留言背景画刷.
    /// </summary>
    public Brush SuperChatBackground => string.IsNullOrEmpty(BackgroundColor) || !TryParseColor(BackgroundColor, out var color)
        ? new SolidColorBrush(Colors.DarkOrange)
        : new SolidColorBrush(color);

    private static bool TryParseColor(string hex, out Windows.UI.Color color)
    {
        color = Colors.Transparent;
        var value = hex.TrimStart('#');
        if (value.Length != 6)
        {
            return false;
        }

        try
        {
            var r = Convert.ToByte(value[..2], 16);
            var g = Convert.ToByte(value.Substring(2, 2), 16);
            var b = Convert.ToByte(value.Substring(4, 2), 16);
            color = Windows.UI.Color.FromArgb(255, r, g, b);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// 礼物榜条目.
/// </summary>
public sealed partial class LiveRankItemViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveRankItemViewModel"/> class.
    /// </summary>
    public LiveRankItemViewModel(LiveRankItem item)
    {
        Rank = item.Rank;
        UserName = item.UserName;
        Avatar = item.Avatar;
        Score = item.Score;
        IsGuard = item.IsGuard;
    }

    /// <summary>
    /// 名次.
    /// </summary>
    public int Rank { get; }

    /// <summary>
    /// 用户名.
    /// </summary>
    public string UserName { get; }

    /// <summary>
    /// 头像.
    /// </summary>
    public string Avatar { get; }

    /// <summary>
    /// 贡献值.
    /// </summary>
    public long Score { get; }

    /// <summary>
    /// 是否舰长.
    /// </summary>
    public bool IsGuard { get; }

    /// <summary>
    /// 名次文本.
    /// </summary>
    public string RankText => Rank.ToString();

    /// <summary>
    /// 贡献文本.
    /// </summary>
    public string ScoreText => Score >= 10000 ? $"{Score / 10000d:F1} 万" : Score.ToString();
}

/// <summary>
/// 大航海条目.
/// </summary>
public sealed partial class LiveGuardItemViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveGuardItemViewModel"/> class.
    /// </summary>
    public LiveGuardItemViewModel(LiveGuardItem item)
    {
        UserName = item.UserName;
        Avatar = item.Avatar;
        GuardLevel = item.GuardLevel;
        TotalWealth = item.TotalWealth;
    }

    /// <summary>
    /// 用户名.
    /// </summary>
    public string UserName { get; }

    /// <summary>
    /// 头像.
    /// </summary>
    public string Avatar { get; }

    /// <summary>
    /// 舰长等级.
    /// </summary>
    public int GuardLevel { get; }

    /// <summary>
    /// 总财富值.
    /// </summary>
    public long TotalWealth { get; }

    /// <summary>
    /// 等级文本.
    /// </summary>
    public string GuardText => GuardLevel switch
    {
        3 => "总督",
        2 => "提督",
        1 => "舰长",
        _ => "船员",
    };
}

/// <summary>
/// 直播流选项（画质 + 线路）.
/// </summary>
public sealed partial class LiveStreamOptionViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveStreamOptionViewModel"/> class.
    /// </summary>
    public LiveStreamOptionViewModel(LiveStreamOption option)
    {
        Option = option;
    }

    /// <summary>
    /// 原始选项.
    /// </summary>
    public LiveStreamOption Option { get; }

    /// <summary>
    /// 画质数值.
    /// </summary>
    public int Quality => Option.Quality;

    /// <summary>
    /// 线路序号.
    /// </summary>
    public int LineIndex => Option.LineIndex;

    /// <summary>
    /// 播放地址.
    /// </summary>
    public string Url => Option.Url;

    /// <summary>
    /// 展示文本（画质 + 线路）.
    /// </summary>
    public string DisplayText
        => $"{GetQualityName(Option.Quality)} · 线路{Option.LineIndex + 1}";

    /// <summary>
    /// 画质名.
    /// </summary>
    public string QualityText => GetQualityName(Option.Quality);

    /// <summary>
    /// 线路文本.
    /// </summary>
    public string LineText => $"线路{Option.LineIndex + 1}";

    /// <summary>
    /// 画质数值转名称.
    /// </summary>
    public static string GetQualityName(int quality)
        => quality switch
        {
            30000 => "杜比",
            20000 => "4K",
            10000 => "原画",
            400 => "蓝光",
            250 => "超清",
            150 => "高清",
            80 => "流畅",
            _ => quality > 0 ? $"Q{quality}" : "默认",
        };
}