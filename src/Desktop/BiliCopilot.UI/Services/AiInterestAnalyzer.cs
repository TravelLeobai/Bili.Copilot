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
            var value = SettingsToolkit.ReadLocalSetting(SettingNames.AiInterestMaxLength, 120);

            // 旧默认值是 60，写不进「动机 + 依据 + 迁移」三层意思，读到时按新默认的 120 处理.
            if (value == 60)
            {
                value = 120;
            }

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
            if (LooksLikeInstructionEcho(content))
            {
                content = string.Empty;
            }
            if (content.Length < 20)
            {
                // 推理模型只把标记写进 content、正文留在 reasoning_content 里.
                content = ExtractSummary(ReadString(message, "reasoning_content"), limit);
                if (LooksLikeInstructionEcho(content))
                {
                    content = string.Empty;
                }
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                logger?.LogWarning("兴趣分析返回内容为空：{Raw}", message.GetRawText().Length > 300 ? message.GetRawText()[..300] : message.GetRawText());
                return null;
            }

            Serilog.Log.Information("兴趣原始输出：{Raw}", content.Length > 200 ? content[..200] : content);
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
        // 内容上不许罗列题材，必须写动机、依据、迁移；文风按 no-ai-slop 的中文规则压一遍。
        // 指令写成短句、不用编号列表：模型会把编号列表当答案复述回来（实测过一次）。
        builder.Append("{\"role\":\"system\",\"content\":\"")
            .Append(Escape("从视频清单里读出这个人的兴趣结构，写两到三句中文。\n"
                + "写动机（他为什么看这些）、依据（从哪些内容看出来的）、以及兴趣是否在迁移。\n"
                + "不要罗列题材类型，「喜欢二次元、数码、历史」等于没说。不要空话，说具体内容。不确定就用「看起来」。\n"
                + "禁用词：赋能、抓手、闭环、生态、链路、沉淀、对齐、颗粒度、底层逻辑、破圈、矩阵、赛道、心智。\n"
                + "禁用句式：不是…而是…、不仅…更是…、综上所述、总而言之、值得一提的是、让我们。\n"
                + "不要排比、感叹号、破折号、结尾升华。\n"
                + $"不超过 {limit} 个汉字，一段话，不分点、不加标题。\n"
                + "下面是格式和深度的参照（只学写法和颗粒度，内容不许照抄）：\n"
                + "<<<兴趣>>>\n"
                + "看起来他在补的是把东西做出来的能力，插件开发、DIY 改造、工具折腾都在同一条线上，考据类只是穿插调剂。早先纯看乐子的 MV 和鬼畜比重在掉。\n"
                + "<<<END>>>\n"
                + "现在照这个颗粒度，为下面的清单写一段。只输出两个标记之间的那段话，不要复述要求，不要解释你在做什么，不要写「我们需要回答」「以下是」。\n"
                + "<<<兴趣>>>\n"
                + "（这里只放那一段话）\n"
                + "<<<END>>>"))
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
            builder.Append(",\"max_tokens\":1500}");
            return builder.ToString();
        }

        text.Append("\n下面附几帧封面作为参考。按前面的要求分析这个人的深层兴趣，不要只列类型。");
        builder.Append("{\"role\":\"user\",\"content\":[");
        builder.Append("{\"type\":\"text\",\"text\":\"").Append(Escape(text.ToString())).Append("\"}");

        foreach (var cover in covers)
        {
            builder.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":\"").Append(Escape(cover)).Append("\"}}");
        }

        builder.Append("]}],\"max_tokens\":1500}");
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
    /// <summary>
    /// 模型有时会把提示词当成答案复述回来，这种整段丢掉，宁可不显示.
    /// </summary>
    /// <param name="text">候选文本.</param>
    /// <returns>是否像在复述要求.</returns>
    private static bool LooksLikeInstructionEcho(string text)
    {
        string[] markers =
        {
            "我们需要回答",
            "不超过",
            "个汉字",
            "任务是",
            "标记之间",
            "以下是",
            "动机、依据",
        };

        return markers.Any(text.Contains);
    }

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
            if (normalized.Length > 0 && !LooksLikeInstructionEcho(normalized))
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
        value = value.Trim().Trim('"', '“', '”', '`', '「', '」', '《', '》').Trim();

        // 模型常在逗号/分号处收尾，留着这些符号会像被截断的残句.
        return value.TrimEnd('，', '、', '；', '：', ',', ';', ':', '—', '-', '…', ' ');
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



