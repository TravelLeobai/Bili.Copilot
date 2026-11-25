// Copyright (c) Bili Copilot. All rights reserved.

using System.Text;

namespace BiliCopilot.UI.Services.Media;

/// <summary>
/// 逻辑媒体身份：媒体字节缓存的唯一 key.
/// <para>
/// 为什么不直接用 BVID 或播放 URL：
/// BVID 分不出同一视频的不同分 P、不同清晰度、不同编码；播放 URL 又不能进 key，
/// 因为 B 站 CDN 地址带签名参数（deadline / upsig / uparams 等），每次取 playurl 都会变，
/// 用 URL 做 key 会让缓存每次都「未命中」，等于没有缓存。
/// </para>
/// <para>
/// 因此 key 由「逻辑字段 + 路径指纹」组成：逻辑字段区分轨道（分 P / 画质 / 编码 / 音视频），
/// 路径指纹负责在同一轨道内区分不同资源，但只取 URL 的 path 部分（丢弃 host 与查询串），
/// 这样换 CDN 镜像、换签名都不会失效。
/// </para>
/// </summary>
public sealed class MediaStreamIdentity : IEquatable<MediaStreamIdentity>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaStreamIdentity"/> class.
    /// </summary>
    /// <param name="bvid">视频 BV 号（番剧可用 epId/seasonId）. 不能为空.</param>
    /// <param name="cid">分 P 的 cid. 不能为空.</param>
    /// <param name="qualityId">画质或音轨 id（dash 里的 Id 字段，如 80 / 30280）.</param>
    /// <param name="codecId">编码 id（如 7=AVC、12=HEVC、13=AV1），未知时为 null.</param>
    /// <param name="mimeType">dash 段的 MimeType，如 video/mp4.</param>
    /// <param name="bandwidth">dash 段标称带宽（bps），未知时为 0.</param>
    /// <param name="initialization">dash 段的 Initialization 字段（仓库目前没用上，先带上，避免将来碰撞）.</param>
    /// <param name="indexRange">dash 段的 IndexRange 字段（同上）.</param>
    /// <param name="fingerprint">稳定指纹，见 <see cref="ComputeFingerprint"/>.</param>
    public MediaStreamIdentity(
        string bvid,
        string cid,
        string qualityId,
        string? codecId,
        string? mimeType,
        long bandwidth,
        string? initialization,
        string? indexRange,
        string fingerprint)
    {
        Bvid = bvid ?? string.Empty;
        Cid = cid ?? string.Empty;
        QualityId = qualityId ?? string.Empty;
        CodecId = codecId ?? string.Empty;
        MimeType = mimeType ?? string.Empty;
        Bandwidth = bandwidth;
        Initialization = initialization ?? string.Empty;
        IndexRange = indexRange ?? string.Empty;
        Fingerprint = fingerprint ?? string.Empty;
    }

    /// <summary>
    /// 视频 BV 号（番剧场景是 epId / seasonId）.
    /// </summary>
    public string Bvid { get; }

    /// <summary>
    /// 分 P 的 cid.
    /// </summary>
    public string Cid { get; }

    /// <summary>
    /// 画质 / 音轨 id.
    /// </summary>
    public string QualityId { get; }

    /// <summary>
    /// 编码 id，未知时为空串.
    /// </summary>
    public string CodecId { get; }

    /// <summary>
    /// dash 段的 MimeType.
    /// </summary>
    public string MimeType { get; }

    /// <summary>
    /// dash 段的标称带宽，未知时为 0.
    /// </summary>
    public long Bandwidth { get; }

    /// <summary>
    /// dash 段的 Initialization，未知时为空串.
    /// </summary>
    public string Initialization { get; }

    /// <summary>
    /// dash 段的 IndexRange，未知时为空串.
    /// </summary>
    public string IndexRange { get; }

    /// <summary>
    /// 稳定指纹（只看 URL 的 path，不含 host / 查询串）.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// 按播放地址算稳定指纹：只保留 path，丢弃 host 与签名查询串.
    /// 不是合法绝对地址时退化为整串的稳定哈希（长度可控）.
    /// </summary>
    /// <param name="url">播放地址.</param>
    /// <returns>指纹，绝不含签名参数.</returns>
    public static string ComputeFingerprint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // AbsolutePath 已剔除查询串与 fragment；CDN 签名都在查询串里.
            return uri.AbsolutePath;
        }

        return "len" + url.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 便捷构造：直接用播放地址算指纹.
    /// </summary>
    /// <param name="bvid">视频 BV 号.</param>
    /// <param name="cid">分 P cid.</param>
    /// <param name="qualityId">画质 / 音轨 id.</param>
    /// <param name="codecId">编码 id.</param>
    /// <param name="mimeType">MimeType.</param>
    /// <param name="bandwidth">标称带宽.</param>
    /// <param name="initialization">dash Initialization.</param>
    /// <param name="indexRange">dash IndexRange.</param>
    /// <param name="url">播放地址（只用来算指纹）.</param>
    /// <returns>身份.</returns>
    public static MediaStreamIdentity Create(
        string bvid,
        string cid,
        string qualityId,
        string? codecId,
        string? mimeType,
        long bandwidth,
        string? initialization,
        string? indexRange,
        string? url)
        => new(bvid, cid, qualityId, codecId, mimeType, bandwidth, initialization, indexRange, ComputeFingerprint(url));

    /// <summary>
    /// 日志用摘要：只含逻辑字段，绝不含播放地址 / 签名参数.
    /// </summary>
    /// <returns>摘要文本.</returns>
    public string ToDiagnosticString()
    {
        var builder = new StringBuilder();
        builder.Append("bvid=").Append(Bvid)
            .Append(",cid=").Append(Cid)
            .Append(",qn=").Append(QualityId)
            .Append(",codec=").Append(CodecId)
            .Append(",mime=").Append(MimeType)
            .Append(",bw=").Append(Bandwidth);
        return builder.ToString();
    }

    /// <inheritdoc/>
    public bool Equals(MediaStreamIdentity? other)
        => other is not null
            && string.Equals(Bvid, other.Bvid, StringComparison.Ordinal)
            && string.Equals(Cid, other.Cid, StringComparison.Ordinal)
            && string.Equals(QualityId, other.QualityId, StringComparison.Ordinal)
            && string.Equals(CodecId, other.CodecId, StringComparison.Ordinal)
            && string.Equals(MimeType, other.MimeType, StringComparison.Ordinal)
            && Bandwidth == other.Bandwidth
            && string.Equals(Initialization, other.Initialization, StringComparison.Ordinal)
            && string.Equals(IndexRange, other.IndexRange, StringComparison.Ordinal)
            && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is MediaStreamIdentity other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Bvid, StringComparer.Ordinal);
        hash.Add(Cid, StringComparer.Ordinal);
        hash.Add(QualityId, StringComparer.Ordinal);
        hash.Add(CodecId, StringComparer.Ordinal);
        hash.Add(MimeType, StringComparer.Ordinal);
        hash.Add(Bandwidth);
        hash.Add(Initialization, StringComparer.Ordinal);
        hash.Add(IndexRange, StringComparer.Ordinal);
        hash.Add(Fingerprint, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => ToDiagnosticString();
}
