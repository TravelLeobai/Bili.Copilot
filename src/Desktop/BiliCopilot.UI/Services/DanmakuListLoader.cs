// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 弹幕列表加载器.
/// 直接读取 B 站弹幕 XML，以拿到弹幕发送者的 UID（内核模型里没有这个字段）.
/// </summary>
internal sealed class DanmakuListLoader
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>
    /// 读取指定分P的全部弹幕.
    /// </summary>
    public async Task<IReadOnlyList<DanmakuListEntry>> LoadAsync(string cid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cid))
        {
            return [];
        }

        var url = $"https://api.bilibili.com/x/v1/dm/list.so?oid={cid}";
        var xml = await HttpClient.GetStringAsync(url, cancellationToken);
        return Parse(xml);
    }

    private static List<DanmakuListEntry> Parse(string xml)
    {
        var result = new List<DanmakuListEntry>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return result;
        }

        try
        {
            var document = XDocument.Parse(xml);
            foreach (var element in document.Descendants("d"))
            {
                var attribute = (string?)element.Attribute("p");
                var content = element.Value?.Trim();
                if (string.IsNullOrEmpty(attribute) || string.IsNullOrEmpty(content))
                {
                    continue;
                }

                var parts = attribute.Split(',');
                if (parts.Length < 7)
                {
                    continue;
                }

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    continue;
                }

                result.Add(new DanmakuListEntry(parts[6], content, seconds));
            }
        }
        catch (Exception)
        {
            return result;
        }

        return result;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        return client;
    }
}

/// <summary>
/// 一条弹幕的原始信息.
/// </summary>
internal readonly record struct DanmakuListEntry(string Uid, string Content, double PositionSeconds);
