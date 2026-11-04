// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Buffers;
using System.Text.Json;
using BiliCopilot.UI.Models.Constants;
using Microsoft.Extensions.Logging;
using BiliCopilot.UI.Toolkits;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.Services;

/// <summary>
/// AI 广告识别.
/// 参考 bilijump-ai：优先用字幕喂给大模型识别广告片段，没有字幕时可选把音频送去阿里云转写后再识别.
/// </summary>
public static class AiAdAnalyzer
{
    private const string DashScopeBase = "https://dashscope.aliyuncs.com";
    private const string DefaultModel = "gemini-2.5-flash";

    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 默认提示词（取自 bilijump-ai，按纯文本字幕做适配）.
    /// </summary>
    public const string DefaultPrompt =
        "你是视频广告识别助手。下面是一个 B 站视频的字幕，每行格式为「开始秒 --> 结束秒\\n字幕文本」。\n" +
        "请找出其中的植入广告段落：\n" +
        "1. 直接推广（赞助、促销、限时优惠、点击链接、关注公众号、下载 App）\n" +
        "2. 口播提及品牌或产品参数（非测评内容）\n" +
        "3. 片头/片尾的固定赞助模板\n" +
        "注意：测评类视频中只提到一两个产品属于正常内容，不要判定为广告；少于 10 秒的片段忽略。\n" +
        "只返回 JSON，不要任何解释，格式：\n" +
        "{\"ads\":[{\"start_time\":335.88,\"end_time\":425.34,\"product_name\":\"产品名\",\"ad_content\":\"广告内容摘要\"}]}\n" +
        "没有广告时返回 {\"ads\":[]}。";

    /// <summary>
    /// 是否启用 AI 识别.
    /// </summary>
    public static bool IsEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.AiAdEnabled, false);

    /// <summary>
    /// 是否允许音频分析.
    /// </summary>
    public static bool IsAudioEnabled
        => SettingsToolkit.ReadLocalSetting(SettingNames.AudioAdEnabled, false);

    /// <summary>
    /// 是否配置了阿里云 DashScope Key（没配就跳过音频识别，别发无效请求）.
    /// </summary>
    public static bool HasDashScopeKey
        => !string.IsNullOrWhiteSpace(SettingsToolkit.ReadLocalSetting(SettingNames.DashScopeKey, string.Empty));

    /// <summary>
    /// 用字幕做广告识别.
    /// </summary>
    /// <param name="subtitleText">字幕文本（含时间轴）.</param>
    /// <param name="logger">日志.</param>
    public static async Task<IReadOnlyList<SponsorSegment>> AnalyzeSubtitleAsync(string subtitleText, ILogger? logger = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(subtitleText))
        {
            return Array.Empty<SponsorSegment>();
        }

        var endpoint = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdEndpoint, string.Empty);
        var key = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdKey, string.Empty);
        var model = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdModel, DefaultModel);
        var prompt = SettingsToolkit.ReadLocalSetting(SettingNames.AiAdPrompt, DefaultPrompt);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
        {
            logger?.LogWarning(
                "AI 广告识别已启用，但没配接口或密钥（endpoint={HasEndpoint} key={HasKey}）",
                !string.IsNullOrWhiteSpace(endpoint),
                !string.IsNullOrWhiteSpace(key));
            return Array.Empty<SponsorSegment>();
        }

        logger?.LogInformation("AI 广告识别请求：{Endpoint} 模型 {Model}", endpoint, model);

        // 字幕过长时截断，避免超出上下文.
        const int maxLength = 48000;
        if (subtitleText.Length > maxLength)
        {
            subtitleText = subtitleText[..maxLength];
        }

        try
        {
            // 注意：裁剪后的程序里 JsonSerializer 禁止反射序列化（匿名对象会直接抛异常），
            // 这里用 JsonSerializer 的 source-gen 不可行，手工拼 JSON 最稳.
            var payloadJson = BuildChatPayload(model, 0.2, prompt, subtitleText);

            var url = endpoint.TrimEnd('/');
            if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                url += "/chat/completions";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            using var response = await Client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("AI 广告识别失败：{Status} {Body}", (int)response.StatusCode, content.Length > 200 ? content[..200] : content);
                return Array.Empty<SponsorSegment>();
            }

            return ParseAds(content, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "AI 广告识别请求异常");
            return Array.Empty<SponsorSegment>();
        }
    }

    /// <summary>
    /// 用音频做识别：先上传到阿里云临时存储，再做语音转写，最后交给大模型.
    /// </summary>
    /// <param name="audioUrl">音频地址（DASH 音轨）.</param>
    /// <param name="cookie">B 站 Cookie.</param>
    /// <param name="logger">日志.</param>
    public static async Task<IReadOnlyList<SponsorSegment>> AnalyzeAudioAsync(string audioUrl, string cookie, ILogger? logger = null)
    {
        if (!IsEnabled || !IsAudioEnabled || string.IsNullOrWhiteSpace(audioUrl))
        {
            return Array.Empty<SponsorSegment>();
        }

        var dashScopeKey = SettingsToolkit.ReadLocalSetting(SettingNames.DashScopeKey, string.Empty);
        var dashScopeModel = SettingsToolkit.ReadLocalSetting(SettingNames.DashScopeModel, "paraformer-v2");
        if (string.IsNullOrWhiteSpace(dashScopeKey))
        {
            logger?.LogDebug("音频分析已启用，但未配置阿里云密钥");
            return Array.Empty<SponsorSegment>();
        }

        try
        {
            var fileUrl = await UploadToDashScopeAsync(audioUrl, cookie, dashScopeKey, dashScopeModel, logger);
            if (string.IsNullOrEmpty(fileUrl))
            {
                return Array.Empty<SponsorSegment>();
            }

            var transcript = await TranscribeAsync(fileUrl, dashScopeKey, dashScopeModel, logger);
            return string.IsNullOrWhiteSpace(transcript)
                ? Array.Empty<SponsorSegment>()
                : await AnalyzeSubtitleAsync(transcript, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "音频广告识别失败");
            return Array.Empty<SponsorSegment>();
        }
    }

    private static IReadOnlyList<SponsorSegment> ParseAds(string content, ILogger? logger)
    {
        var result = new List<SponsorSegment>();
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            // OpenAI 兼容响应：choices[0].message.content 里是模型回答.
            var text = content;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                text = message.GetProperty("content").GetString() ?? string.Empty;
            }

            var jsonStart = text.IndexOf('{');
            var jsonEnd = text.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
            {
                return result;
            }

            using var adsDocument = JsonDocument.Parse(text[jsonStart..(jsonEnd + 1)]);
            if (!adsDocument.RootElement.TryGetProperty("ads", out var ads) || ads.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in ads.EnumerateArray())
            {
                var start = ReadDouble(item, "start_time");
                var end = ReadDouble(item, "end_time");
                if (start is null || end is null || end <= start)
                {
                    continue;
                }

                result.Add(new SponsorSegment
                {
                    Start = start.Value,
                    End = end.Value,
                    Category = "ai",
                    Source = "ai",
                });
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "AI 返回内容解析失败");
        }

        return result;
    }

    private static async Task<string?> UploadToDashScopeAsync(
        string audioUrl,
        string cookie,
        string apiKey,
        string model,
        ILogger? logger)
    {
        using var audioResponse = await SendWithBiliHeadersAsync(audioUrl, cookie);
        if (!audioResponse.IsSuccessStatusCode)
        {
            logger?.LogWarning("下载音频失败：{Status}", (int)audioResponse.StatusCode);
            return null;
        }

        var audioBytes = await audioResponse.Content.ReadAsByteArrayAsync();
        using var policyRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{DashScopeBase}/api/v1/uploads?action=getPolicy&model={WebUtility.UrlEncode(model)}");
        policyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var policyResponse = await Client.SendAsync(policyRequest);
        if (!policyResponse.IsSuccessStatusCode)
        {
            logger?.LogWarning("获取上传策略失败：{Status}", (int)policyResponse.StatusCode);
            return null;
        }

        using var policyDocument = JsonDocument.Parse(await policyResponse.Content.ReadAsStringAsync());
        if (!policyDocument.RootElement.TryGetProperty("data", out var policy))
        {
            return null;
        }

        var uploadHost = policy.GetProperty("upload_host").GetString();
        var uploadDir = policy.GetProperty("upload_dir").GetString();
        if (string.IsNullOrEmpty(uploadHost) || string.IsNullOrEmpty(uploadDir))
        {
            return null;
        }

        var key = $"{uploadDir}/{Guid.NewGuid():N}.m4a";
        var form = new MultipartFormDataContent
        {
            { new StringContent(policy.GetProperty("oss_access_key_id").GetString() ?? string.Empty), "OSSAccessKeyId" },
            { new StringContent(policy.GetProperty("signature").GetString() ?? string.Empty), "Signature" },
            { new StringContent(policy.GetProperty("policy").GetString() ?? string.Empty), "policy" },
            { new StringContent(policy.GetProperty("x_oss_object_acl").GetString() ?? "private"), "x-oss-object-acl" },
            { new StringContent(policy.GetProperty("x_oss_forbid_overwrite").GetString() ?? "true"), "x-oss-forbid-overwrite" },
            { new StringContent(key), "key" },
            { new StringContent("200"), "success_action_status" },
        };

        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
        form.Add(fileContent, "file", Path.GetFileName(key));

        using var uploadResponse = await Client.PostAsync(uploadHost, form);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            logger?.LogWarning("上传音频到临时存储失败：{Status}", (int)uploadResponse.StatusCode);
            return null;
        }

        return $"oss://{key}";
    }

    private static async Task<string?> TranscribeAsync(string fileUrl, string apiKey, string model, ILogger? logger)
    {
        var submitPayload = BuildTranscriptionPayload(model, fileUrl);

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{DashScopeBase}/api/v1/services/audio/asr/transcription");
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        submitRequest.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
        submitRequest.Content = new StringContent(submitPayload, Encoding.UTF8, "application/json");

        using var submitResponse = await Client.SendAsync(submitRequest);
        var submitBody = await submitResponse.Content.ReadAsStringAsync();
        if (!submitResponse.IsSuccessStatusCode)
        {
            logger?.LogWarning("提交音频转写任务失败：{Status} {Body}", (int)submitResponse.StatusCode, submitBody.Length > 160 ? submitBody[..160] : submitBody);
            return null;
        }

        using var submitDocument = JsonDocument.Parse(submitBody);
        if (!submitDocument.RootElement.TryGetProperty("output", out var output)
            || !output.TryGetProperty("task_id", out var taskIdNode))
        {
            return null;
        }

        var taskId = taskIdNode.GetString();
        if (string.IsNullOrEmpty(taskId))
        {
            return null;
        }

        // 轮询任务结果，最多等 5 分钟.
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{DashScopeBase}/api/v1/tasks/{taskId}");
            statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var statusResponse = await Client.SendAsync(statusRequest);
            if (!statusResponse.IsSuccessStatusCode)
            {
                continue;
            }

            using var statusDocument = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            if (!statusDocument.RootElement.TryGetProperty("output", out var statusOutput)
                || !statusOutput.TryGetProperty("task_status", out var statusNode))
            {
                continue;
            }

            var status = statusNode.GetString();
            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!statusOutput.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return null;
            }

            var transcriptionUrl = results[0].TryGetProperty("transcription_url", out var urlNode)
                ? urlNode.GetString()
                : null;
            if (string.IsNullOrEmpty(transcriptionUrl))
            {
                return null;
            }

            using var transcriptResponse = await Client.GetAsync(transcriptionUrl);
            if (!transcriptResponse.IsSuccessStatusCode)
            {
                return null;
            }

            return ParseTranscript(await transcriptResponse.Content.ReadAsStringAsync());
        }

        logger?.LogWarning("音频转写超时");
        return null;
    }

    private static string? ParseTranscript(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("transcripts", out var transcripts)
                || transcripts.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var item in transcripts.EnumerateArray())
            {
                if (!item.TryGetProperty("sentences", out var sentences) || sentences.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var sentence in sentences.EnumerateArray())
                {
                    var begin = sentence.TryGetProperty("begin_time", out var beginNode) ? beginNode.GetInt64() / 1000d : 0d;
                    var end = sentence.TryGetProperty("end_time", out var endNode) ? endNode.GetInt64() / 1000d : 0d;
                    var text = sentence.TryGetProperty("text", out var textNode) ? textNode.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    builder.AppendLine($"{begin:F2} --> {end:F2}");
                    builder.AppendLine(text);
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<HttpResponseMessage> SendWithBiliHeadersAsync(string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return await Client.SendAsync(request);
    }

    private static double? ReadDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var node))
        {
            return null;
        }

        return node.ValueKind switch
        {
            JsonValueKind.Number => node.GetDouble(),
            JsonValueKind.String => double.TryParse(node.GetString(), out var value) ? value : null,
            _ => null,
        };
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }
    /// <summary>
    /// 手工拼 chat/completions 请求体（避开裁剪程序的反射序列化限制）.
    /// </summary>
    private static string BuildChatPayload(string model, double temperature, string systemPrompt, string userContent)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteNumber("temperature", temperature);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", systemPrompt);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", userContent);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 手工拼阿里云语音转写任务提交体.
    /// </summary>
    private static string BuildTranscriptionPayload(string model, string fileUrl)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartObject("input");
            writer.WriteStartArray("file_urls");
            writer.WriteStringValue(fileUrl);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartObject("parameters");
            writer.WriteStartArray("channel_id");
            writer.WriteNumberValue(0);
            writer.WriteEndArray();
            writer.WriteString("language", "zh");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
