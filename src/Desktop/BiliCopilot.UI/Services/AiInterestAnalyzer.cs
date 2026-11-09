// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Services.Live;
using BiliCopilot.UI.Toolkits;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 已看过视频的观看兴趣分析.
/// 复用「空降助手」里配置的 OpenAI 兼容接口与模型，把已看过视频的标题、封面、简介
/// 一起发给模型，让它用一段话概括最近的观看偏好.
/// </summary>
internal static class AiInterestAnalyzer
{
    /// <summary>
    /// 是否启用兴趣分析.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.AiInterestEnabled, true);

    /// <summary>
    /// 结论正文的字数上限（设置页可调，默认 60 字）.
    /// 提示词要求按标记输出，解析时再按这个上限硬裁一次，模型不听话时也不会跑偏.
    /// </summary>
    public static int MaximumLength
    {
        get
        {
            var value = SettingsToolkit.ReadLocalSetting(SettingNames.AiInterestMaxLength, 60);
            return Math.Clamp(value, 20, 200);
        }
    }

    /// <summary>
    /// 接口与密钥是否已经配好（没配就直接跳过，不发请求）.
    /// </summary>
    public static bool IsConfigured
    {
        get
        {
            var endpoint = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdEndpoint, string.Empty);
            var key = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdKey, string.Empty);
            return !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key);
        }
    }

    private static bool _visionUnsupported;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = IpV4Connector.ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// 分析观看兴趣.
    /// </summary>
    /// <param name="samples">已看过的视频（标题 / 封面 / 简介）.最多取前若干个.</param>
    /// <param name="logger">日志.</param>
    /// <returns>一段中文描述，失败返回 null.</returns>
    public static async Task<string?> AnalyzeAsync(
        IReadOnlyList<InterestSample> samples,
        ILogger? logger = null)
    {
        if (!IsEnabled || !IsConfigured || samples.Count == 0)
        {
            return null;
        }

        var endpoint = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdEndpoint, string.Empty);
        var key = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdKey, string.Empty);
        var model = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdModel, string.Empty);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-4o-mini";
        }

        var selected = samples.Take(12).ToList();
        var covers = _visionUnsupported ? [] : await CollectCoversAsync(selected, logger).ConfigureAwait(false);
        var limit = MaximumLength;
        var result = await RequestAsync(endpoint, key, model, selected, covers, limit, logger).ConfigureAwait(false);
        if (result is null && covers.Count > 0)
        {
            // 模型不支持图片时退回纯文本再试一次.
            logger?.LogInformation("兴趣分析含图片失败，改用纯文本重试");
            _visionUnsupported = true;
            result = await RequestAsync(endpoint, key, model, selected, [], limit, logger).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task<string?> RequestAsync(
        string endpoint,
        string key,
        string model,
        IReadOnlyList<InterestSample> samples,
        IReadOnlyList<string> covers,
        int limit,
        ILogger? logger)
    {
        try
        {
            var body = BuildRequestBody(model, samples, covers, limit);
            using var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl(endpoint))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("兴趣分析请求失败：HTTP {Status} {Body}", (int)response.StatusCode, text.Length > 200 ? text[..200] : text);
                return null;
            }

            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                logger?.LogWarning("兴趣分析返回里没有 choices：{Body}", text.Length > 240 ? text[..240] : text);
                return null;
            }

            var message = choices[0].GetProperty("message");
            var content = ReadContent(message);

            content = ExtractSummary(content, limit);
            if (content.Length < 20)
            {
                // 推理模型只把标记写进 content、正文留在 reasoning_content 里.
                content = ExtractSummary(ReadString(message, "reasoning_content"), limit);
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                logger?.LogWarning("兴趣分析返回内容为空：{Raw}", message.GetRawText().Length > 300 ? message.GetRawText()[..300] : message.GetRawText());
                return null;
            }

            logger?.LogInformation("观看兴趣分析完成：{Length} 字", content.Length);
            return content;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "兴趣分析请求异常");
            return null;
        }
    }

    /// <summary>
    /// 手工拼请求体：裁剪构建里 JsonSerializer 不可用.
    /// </summary>
    private static string BuildRequestBody(string model, IReadOnlyList<InterestSample> samples, IReadOnlyList<string> covers, int limit)
    {
        var builder = new StringBuilder();
        builder.Append("{\"model\":\"").Append(Escape(model)).Append("\",\"temperature\":0.4,\"messages\":[");

        // 系统提示：严格限定输出格式，正文只放在 <<<兴趣>>> ... <<<END>>> 之间.
        builder.Append("{\"role\":\"system\",\"content\":\"")
            .Append(Escape("你是视频平台的内容分析师。根据用户最近看过的视频（标题、封面、简介），概括他最近的内容偏好。"
                + $"输出必须严格按下面的格式，只允许一段话，不超过 {limit} 个汉字：\n"
                + "<<<兴趣>>>\n"
                + "（只在这里写那一段话）\n"
                + "<<<END>>>\n"
                + "规则：不要写解释、不要写标题、不要分点、不要罗列视频名、不要加引号或 Markdown；"
                + "除这两个标记和中间那段话之外，不要输出任何其他字符。"))
            .Append("\"},");

        var text = new StringBuilder("最近看过的视频：\n");
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            text.Append(i + 1).Append(". ").Append(sample.Title);
            if (!string.IsNullOrWhiteSpace(sample.Author))
            {
                text.Append("（UP 主：").Append(sample.Author).Append('）');
            }

            if (!string.IsNullOrWhiteSpace(sample.Description))
            {
                var desc = sample.Description.Length > 80 ? sample.Description[..80] : sample.Description;
                text.Append(" — ").Append(desc.Replace("\r", " ").Replace("\n", " "));
            }

            text.Append('\n');
        }

        // 没有封面时用纯字符串 content，非视觉模型只认这种格式.
        if (covers.Count == 0)
        {
            builder.Append("{\"role\":\"user\",\"content\":\"").Append(Escape(text.ToString())).Append("\"}]");
            builder.Append(",\"max_tokens\":400}");
            return builder.ToString();
        }

        text.Append("\n下面附几帧封面作为参考。请据此概括这个人的观看兴趣。");
        builder.Append("{\"role\":\"user\",\"content\":[");
        builder.Append("{\"type\":\"text\",\"text\":\"").Append(Escape(text.ToString())).Append("\"}");

        foreach (var cover in covers)
        {
            builder.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":\"").Append(Escape(cover)).Append("\"}}");
        }

        builder.Append("]}],\"max_tokens\":400}");
        return builder.ToString();
    }
    private static async Task<List<string>> CollectCoversAsync(IReadOnlyList<InterestSample> samples, ILogger? logger)
    {
        var covers = new List<string>();
        foreach (var sample in samples.Where(p => !string.IsNullOrWhiteSpace(p.CoverUrl)).Take(3))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, sample.CoverUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                using var response = await Client.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length is 0 or > 900_000)
                {
                    continue;
                }

                covers.Add($"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}");
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "下载封面失败：{Url}", sample.CoverUrl);
            }
        }

        return covers;
    }

    private static string NormalizeUrl(string endpoint)
    {
        var url = endpoint.Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{url}/chat/completions";
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>
    /// 兼容两种 content 形态：字符串，或 [{type:"text",text:"…"}] 数组.
    /// </summary>
    private static string ReadContent(JsonElement message)
    {
        var text = ReadString(message, "content");
        if (string.IsNullOrWhiteSpace(text))
        {
            // 推理类模型常把正文放在 reasoning_content 里.
            text = ReadString(message, "reasoning_content");
        }

        return text;
    }

    private static string ReadString(JsonElement message, string name)
    {
        if (!message.TryGetProperty(name, out var node) || node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            return node.GetString() ?? string.Empty;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in node.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out var textNode)
                    && textNode.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textNode.GetString());
                }
            }

            return builder.ToString();
        }

        return string.Empty;
    }

    /// <summary>
    /// 从模型输出里抽正文：优先严格取标记「兴趣」块之间的内容；
    /// 模型没按格式来时，退回「【结论】」/「结论：」之后的文字；最后按字数上限硬裁.
    /// </summary>
    private static string ExtractSummary(string raw, int limit)
    {
        var text = raw.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 1) 严格格式：只认标记之间的内容.
        const string startMarker = "<<<兴趣>>>";
        const string endMarker = "<<<END>>>";
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        if (start >= 0)
        {
            var bodyStart = start + startMarker.Length;
            var end = text.IndexOf(endMarker, bodyStart, StringComparison.Ordinal);
            var body = end > bodyStart ? text[bodyStart..end] : text[bodyStart..];
            var normalized = Normalize(body);
            if (normalized.Length > 0)
            {
                return TrimToLimit(normalized, limit);
            }
        }

        // 2) 兼容旧格式.
        var markerIndex = text.LastIndexOf("【结论】", StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            text = text[(markerIndex + 4)..];
        }
        else
        {
            var colonIndex = text.LastIndexOf("结论：", StringComparison.Ordinal);
            if (colonIndex >= 0)
            {
                text = text[(colonIndex + 3)..];
            }
        }

        // 推理模型会把英文思考混进正文：按句子筛，只留以中文为主的那些.
        var sentences = SplitSentences(text);
        var selected = sentences.Where(p => CountChinese(p) >= 6 && CountChinese(p) * 2 >= p.Length).ToList();
        var result = selected.Count > 0 ? string.Concat(selected) : text;
        return TrimToLimit(Normalize(result), limit);
    }

    /// <summary>
    /// 清掉模型附带的 Markdown / 引号 / 多余空行，只留纯文本.
    /// </summary>
    /// <param name="text">原始文本.</param>
    /// <returns>纯文本.</returns>
    private static string Normalize(string text)
    {
        var value = text.Replace("\r", "\n").Trim();
        // 去掉行首的列表符和标题符.
        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().TrimStart('-', '*', '#', '>', ' ').Trim())
            .Where(p => p.Length > 0);
        value = string.Join(string.Empty, lines);
        return value.Trim().Trim('"', '“', '”', '`', '「', '」', '《', '》').Trim();
    }

    /// <summary>
    /// 按字数上限裁剪：优先在句号/逗号处收口，避免半句话.
    /// </summary>
    /// <param name="text">文本.</param>
    /// <param name="limit">上限.</param>
    /// <returns>裁剪后的文本.</returns>
    private static string TrimToLimit(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var window = text[..limit];
        var cut = window.LastIndexOfAny(['。', '！', '？', '…', '；']);
        if (cut < limit / 2)
        {
            cut = window.LastIndexOfAny(['，', '、', ',']);
        }

        return cut >= limit / 2 ? text[..(cut + 1)].Trim() : window.Trim() + "…";
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            builder.Append(c);
            if (c is '。' or '！' or '？' or '；' or '…' or '\n' or '\r')
            {
                var sentence = builder.ToString().Trim();
                if (sentence.Length > 0)
                {
                    yield return sentence;
                }

                builder.Clear();
            }
        }

        var tail = builder.ToString().Trim();
        if (tail.Length > 0)
        {
            yield return tail;
        }
    }

    private static int CountChinese(string value)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c >= '\u4e00' && c <= '\u9fff')
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// 参与兴趣分析的一条视频信息.
/// </summary>
/// <param name="Title">标题.</param>
/// <param name="Author">UP 主.</param>
/// <param name="Description">简介片段.</param>
/// <param name="CoverUrl">封面地址.</param>
public sealed record InterestSample(string Title, string Author, string Description, string CoverUrl);


