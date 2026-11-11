// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Concurrent;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 评论里的一张配图.
/// </summary>
/// <param name="Url">图片地址.</param>
/// <param name="Width">原始宽度.</param>
/// <param name="Height">原始高度.</param>
public sealed record CommentPicture(string Url, int Width, int Height)
{
    /// <summary>
    /// 宽高比，用于按固定高度算显示宽度；拿不到尺寸时按 4:3 估.
    /// </summary>
    public double AspectRatio
        => Width > 0 && Height > 0 ? (double)Width / Height : 4d / 3d;
}

/// <summary>
/// 评论附图缓存：内核的 CommentInformation 不带 pictures 字段，
/// 只能从 x/v2/reply 的原始 JSON 里取，按评论 id 存下来供渲染层命中.
/// </summary>
public static class CommentPictureStore
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<CommentPicture>> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 登记一条评论的附图.
    /// </summary>
    public static void Register(string id, IReadOnlyList<CommentPicture> pictures)
    {
        if (string.IsNullOrEmpty(id) || pictures.Count == 0)
        {
            return;
        }

        Cache[id] = pictures;
    }

    /// <summary>
    /// 取一条评论的附图，没有就返回空集合.
    /// </summary>
    public static IReadOnlyList<CommentPicture> Get(string? id)
        => !string.IsNullOrEmpty(id) && Cache.TryGetValue(id, out var pictures) ? pictures : [];
}

