// Copyright (c) Bili Copilot. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 弹幕列表条目视图模型.
/// </summary>
public sealed partial class DanmakuListItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _userName;

    [ObservableProperty]
    private Uri? _avatar;

    [ObservableProperty]
    private int _level;

    [ObservableProperty]
    private bool _isBlocked;

    /// <summary>
    /// Initializes a new instance of the <see cref="DanmakuListItemViewModel"/> class.
    /// </summary>
    public DanmakuListItemViewModel(string uid, string content, double positionSeconds)
    {
        Uid = uid;
        Content = content;
        PositionSeconds = positionSeconds;
        UserName = uid;
    }

    /// <summary>
    /// 发送者 UID.
    /// </summary>
    public string Uid { get; }

    /// <summary>
    /// 弹幕内容.
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// 出现时间（秒）.
    /// </summary>
    public double PositionSeconds { get; }

    /// <summary>
    /// 时间戳文本.
    /// </summary>
    public string TimeText => TimeSpan.FromSeconds(PositionSeconds).ToString(@"hh\:mm\:ss");

    /// <summary>
    /// 用户信息是否已经解析出来.
    /// </summary>
    public bool IsUserResolved { get; set; }

    /// <summary>
    /// 等级文本.
    /// </summary>
    public string LevelText => Level > 0 ? $"LV{Level}" : string.Empty;
}
