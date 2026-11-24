// Copyright (c) Bili Copilot. All rights reserved.

using System.Text.Json;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 互动消息类型.
/// </summary>
public enum LiveInteractionKind
{
    /// <summary>普通弹幕.</summary>
    Danmaku,

    /// <summary>礼物.</summary>
    Gift,

    /// <summary>醒目留言.</summary>
    SuperChat,

    /// <summary>上舰.</summary>
    Guard,

    /// <summary>进场/关注等互动.</summary>
    Enter,

    /// <summary>系统提示.</summary>
    System,
}

/// <summary>
/// 直播间互动消息.
/// </summary>
public sealed class LiveInteractionItem
{
    public LiveInteractionKind Kind { get; set; }

    public string Command { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Avatar { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public int UserLevel { get; set; }

    public int MedalLevel { get; set; }

    public string MedalName { get; set; } = string.Empty;

    public string GiftName { get; set; } = string.Empty;

    public int GiftCount { get; set; }

    public int GiftPrice { get; set; }

    public string CoinType { get; set; } = string.Empty;

    public int GuardLevel { get; set; }

    public string BackgroundColor { get; set; } = string.Empty;

    /// <summary>
    /// 表情弹幕的图片地址（普通弹幕为空）.
    /// </summary>
    public string EmoteUrl { get; set; } = string.Empty;

    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 用于列表展示的主文本.
    /// </summary>
    public string DisplayText => Kind switch
    {
        LiveInteractionKind.Gift => $"{UserName} 投喂 {GiftName} × {GiftCount}",
        LiveInteractionKind.SuperChat => $"{UserName} 发送了 ¥{GiftPrice} 的醒目留言：{Text}",
        LiveInteractionKind.Guard => $"{UserName} 开通了 {GiftName} × {GiftCount}",
        LiveInteractionKind.Enter => Text,
        _ => Text,
    };
}

/// <summary>
/// 直播间消息解析.
/// </summary>
public static class LiveMessageParser
{
    /// <summary>
    /// 表情对象在 info[0] 里的候选下标（实测 12，个别版本 13/11/14）.
    /// 提取成静态字段，避免每条弹幕都分配一个新数组.
    /// </summary>
    private static readonly int[] EmoteIndexProbe = [12, 13, 11, 14];

    /// <summary>
    /// 解析一条消息；不认识的命令返回 null.
    /// </summary>
    public static LiveInteractionItem? Parse(string command, JsonElement root)
    {
        // 直播命令常带后缀（DANMU_MSG:4:0:2:2:2:0），只比较冒号前那段。
        // 原来用 Split(':')[0]，每条消息要分配一个 string[] 加一个子串；
        // 改成分发前先切一个只读视图，两个分配都省掉。
        var head = command.AsSpan();
        var colon = head.IndexOf(':');
        if (colon >= 0)
        {
            head = head[..colon];
        }

        if (head.SequenceEqual("DANMU_MSG"))
        {
            return ParseDanmaku(root);
        }

        if (head.SequenceEqual("SEND_GIFT"))
        {
            return ParseGift(root);
        }

        if (head.SequenceEqual("SUPER_CHAT_MESSAGE") || head.SequenceEqual("SUPER_CHAT_MESSAGE_JPN"))
        {
            return ParseSuperChat(root);
        }

        if (head.SequenceEqual("GUARD_BUY"))
        {
            return ParseGuard(root);
        }

        if (head.SequenceEqual("INTERACT_WORD") || head.SequenceEqual("INTERACT_WORD_V2"))
        {
            return ParseInteract(root);
        }

        return null;
    }

    private static LiveInteractionItem? ParseDanmaku(JsonElement root)
    {
        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array || info.GetArrayLength() < 3)
        {
            return null;
        }

        var item = new LiveInteractionItem
        {
            Kind = LiveInteractionKind.Danmaku,
            Command = "DANMU_MSG",
            Text = info[1].GetString() ?? string.Empty,
        };

        if (info[2].ValueKind == JsonValueKind.Array && info[2].GetArrayLength() > 1)
        {
            item.UserName = info[2][1].GetString() ?? string.Empty;
        }

        // info[0] 是用户信息数组，头像和表情都要从它上面读；提成局部变量，
        // 少几次「从数组头重走一遍元数据」的定位（每条弹幕都走这条路）.
        var userInfo = info[0];

        // info[0][15] 是用户信息对象，弹幕列表的头像就靠它（uinfo.base.face，老协议在 uinfo.face）.
        if (userInfo.ValueKind == JsonValueKind.Array
            && userInfo.GetArrayLength() > 15
            && userInfo[15].ValueKind == JsonValueKind.Object
            && userInfo[15].TryGetProperty("user", out var danmakuUser))
        {
            if (danmakuUser.TryGetProperty("base", out var danmakuBase))
            {
                item.Avatar = ReadString(danmakuBase, "face");
            }

            if (string.IsNullOrEmpty(item.Avatar))
            {
                item.Avatar = ReadString(danmakuUser, "face");
            }
        }

        // info[3] = 粉丝勋章 [等级, 名称, 主播名, roomid, 颜色...]
        if (info.GetArrayLength() > 3 && info[3].ValueKind == JsonValueKind.Array && info[3].GetArrayLength() > 1)
        {
            item.MedalLevel = info[3][0].ValueKind == JsonValueKind.Number ? info[3][0].GetInt32() : 0;
            item.MedalName = info[3][1].ValueKind == JsonValueKind.String ? info[3][1].GetString() ?? string.Empty : string.Empty;
        }

        // 表情弹幕：实测表情对象挂在 info[0][12]（个别版本在 13），
        // 里面有 emoticon_unique 和 url，列表按这个 url 画图.
        if (userInfo.ValueKind == JsonValueKind.Array)
        {
            foreach (var index in EmoteIndexProbe)
            {
                if (userInfo.GetArrayLength() <= index || userInfo[index].ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var emoteNode = userInfo[index];
                var unique = ReadString(emoteNode, "emoticon_unique");
                if (string.IsNullOrEmpty(unique))
                {
                    continue;
                }

                var emoteUrl = ReadString(emoteNode, "url");
                if (string.IsNullOrEmpty(emoteUrl))
                {
                    continue;
                }

                // 接口给的是 http，WinUI 加载会被拦，统一换成 https.
                item.EmoteUrl = emoteUrl.StartsWith("//", StringComparison.Ordinal)
                    ? "https:" + emoteUrl
                    : emoteUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        ? "https://" + emoteUrl["http://".Length..]
                        : emoteUrl;
                break;
            }
        }

        // info[4] = 用户等级 [等级, ...]
        if (info.GetArrayLength() > 4 && info[4].ValueKind == JsonValueKind.Array && info[4].GetArrayLength() > 0 && info[4][0].ValueKind == JsonValueKind.Number)
        {
            item.UserLevel = info[4][0].GetInt32();
        }

        return item;
    }

    private static LiveInteractionItem? ParseGift(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return null;
        }

        var item = new LiveInteractionItem
        {
            Kind = LiveInteractionKind.Gift,
            Command = "SEND_GIFT",
            UserName = ReadString(data, "uname"),
            GiftName = ReadString(data, "giftName"),
            GiftCount = ReadInt(data, "num"),
            GiftPrice = ReadInt(data, "price"),
            CoinType = ReadString(data, "coin_type"),
            Avatar = ReadString(data, "face"),
        };

        if (data.TryGetProperty("medal_info", out var medal))
        {
            item.MedalLevel = ReadInt(medal, "medal_level");
            item.MedalName = ReadString(medal, "medal_name");
        }

        return item;
    }

    private static LiveInteractionItem? ParseSuperChat(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return null;
        }

        var item = new LiveInteractionItem
        {
            Kind = LiveInteractionKind.SuperChat,
            Command = "SUPER_CHAT_MESSAGE",
            Text = ReadString(data, "message"),
            GiftPrice = ReadInt(data, "price"),
            BackgroundColor = ReadString(data, "background_color"),
        };

        if (data.TryGetProperty("user_info", out var user))
        {
            item.UserName = ReadString(user, "uname");
            item.Avatar = ReadString(user, "face");
        }

        if (data.TryGetProperty("medal_info", out var medal))
        {
            item.MedalLevel = ReadInt(medal, "medal_level");
            item.MedalName = ReadString(medal, "medal_name");
        }

        return item;
    }

    private static LiveInteractionItem? ParseGuard(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return null;
        }

        return new LiveInteractionItem
        {
            Kind = LiveInteractionKind.Guard,
            Command = "GUARD_BUY",
            UserName = ReadString(data, "uname"),
            GiftName = ReadString(data, "gift_name"),
            GiftCount = ReadInt(data, "num"),
            GiftPrice = ReadInt(data, "price"),
            GuardLevel = ReadInt(data, "guard_level"),
        };
    }

    private static LiveInteractionItem? ParseInteract(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return null;
        }

        var item = new LiveInteractionItem
        {
            Kind = LiveInteractionKind.Enter,
            Command = "INTERACT_WORD",
        };

        if (data.TryGetProperty("uinfo", out var uinfo) && uinfo.TryGetProperty("base", out var baseInfo))
        {
            item.UserName = ReadString(baseInfo, "name");
            item.Avatar = ReadString(baseInfo, "face");
        }

        // V2 把用户信息塞进 pb（protobuf base64），JSON 里没有名字.
        if (string.IsNullOrEmpty(item.UserName)
            && data.TryGetProperty("pb", out var pbNode)
            && pbNode.ValueKind == JsonValueKind.String)
        {
            foreach (var text in LiveProtobufHelper.ExtractStrings(pbNode.GetString() ?? string.Empty))
            {
                if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(item.Avatar))
                    {
                        item.Avatar = text;
                    }

                    continue;
                }

                if (string.IsNullOrEmpty(item.UserName) && !text.Contains('/') && text.Length <= 40)
                {
                    item.UserName = text;
                }
            }
        }

        if (string.IsNullOrEmpty(item.UserName))
        {
            item.UserName = ReadString(data, "uname");
        }

        var msgType = ReadInt(data, "msg_type");
        item.Text = msgType switch
        {
            1 => $"{item.UserName} 进入了直播间",
            2 => $"{item.UserName} 关注了主播",
            3 => $"{item.UserName} 分享了直播间",
            4 => $"{item.UserName} 特别关注了主播",
            5 => $"{item.UserName} 和主播互粉了",
            _ => $"{item.UserName} 进入了直播间",
        };

        return item;
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() ?? string.Empty : string.Empty;

    private static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var node) && node.TryGetInt32(out var value) ? value : 0;
}