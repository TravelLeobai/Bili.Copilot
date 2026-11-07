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
    /// 字幕识别用的模型名（给界面上的过程日志用）.
    /// </summary>
    public static string CurrentSubtitleModel
        => SettingsToolkit.ReadLocalSetting(SettingNames.AiAdModel, DefaultModel);

    /// <summary>
    /// 音频转写用的模型名.
    /// </summary>
    public static string CurrentAudioModel
        => SettingsToolkit.ReadLocalSetting(SettingNames.DashScopeModel, "paraformer-v2");

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
    /// <param name="onStage">过程回调，用来把每步写进界面日志.</param>
    public static async Task<IReadOnlyList<SponsorSegment>> AnalyzeSubtitleAsync(string subtitleText, ILogger? logger = null, Action<string>? onStage = null)
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
        onStage?.Invoke($"AI 字幕：发送 {subtitleText.Length} 字 → {model}");

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
                onStage?.Invoke($"AI 字幕：请求失败 {(int)response.StatusCode}");
                return Array.Empty<SponsorSegment>();
            }

            var ads = ParseAds(content, logger);
            onStage?.Invoke($"AI 字幕：{model} 返回 {ads.Count} 段");
            return ads;
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
    /// <param name="onStage">过程回调，用来把每步写进界面日志.</param>
    public static async Task<IReadOnlyList<SponsorSegment>> AnalyzeAudioAsync(string audioUrl, string cookie, ILogger? logger = null, Action<string>? onStage = null)
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
            // 先上传再转写：B 站 CDN 要 Referer，让 DashScope 直拉永远 FILE_403，
            // 白等一轮还留一个失败任务，所以上传放前面，直拉只当兜底.
            string? transcript = null;
            onStage?.Invoke("音频：上传到临时存储");
            var fileUrl = await UploadToDashScopeAsync(audioUrl, cookie, dashScopeKey, dashScopeModel, logger);
            if (!string.IsNullOrEmpty(fileUrl))
            {
                onStage?.Invoke("音频：上传完成，提交转写任务");
                transcript = await TranscribeAsync(fileUrl, dashScopeKey, dashScopeModel, logger);
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                logger?.LogInformation("上传通道没拿到转写结果，改让 DashScope 直拉音频");
                onStage?.Invoke("音频：上传通道没结果，试直拉音轨");
                transcript = await TranscribeAsync(audioUrl, dashScopeKey, dashScopeModel, logger);
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                onStage?.Invoke("音频：转写没拿到文本，音频识别放弃");
                return Array.Empty<SponsorSegment>();
            }

            onStage?.Invoke($"音频：转写得到 {transcript.Length} 字，交给 {CurrentSubtitleModel}");
            return await AnalyzeSubtitleAsync(transcript, logger, onStage);
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

        var audioBytes = await ReadMediaBytesAsync(audioResponse, logger);
        if (audioBytes is null)
        {
            return null;
        }
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

        // policy 里给的 host 有时不带协议，带上再发，否则 HttpClient 会当成相对地址.
        if (!uploadHost.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            uploadHost = "https://" + uploadHost;
        }

        uploadHost = uploadHost.TrimEnd('/') + "/";

        var key = $"{uploadDir}/{Guid.NewGuid():N}.m4a";
        var fileName = Path.GetFileName(key);

        // 自己拼 multipart：.NET 的 MultipartFormDataContent 会在 Content-Type 里给 boundary
        // 加引号，OSS 直接判 MalformedPOSTRequest（实测 400）。这里手写请求体，
        // boundary 不加引号，字段顺序按 OSS PostObject 的惯例把 file 放最后.
        var boundary = "----BiliCopilot" + Guid.NewGuid().ToString("N");
        using var bodyStream = new MemoryStream();

        async Task WriteFieldAsync(string name, string value)
        {
            var head = $"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n";
            await bodyStream.WriteAsync(Encoding.UTF8.GetBytes(head));
        }

        await WriteFieldAsync("OSSAccessKeyId", policy.GetProperty("oss_access_key_id").GetString() ?? string.Empty);
        await WriteFieldAsync("Signature", policy.GetProperty("signature").GetString() ?? string.Empty);
        await WriteFieldAsync("policy", policy.GetProperty("policy").GetString() ?? string.Empty);
        await WriteFieldAsync("x-oss-object-acl", GetPolicyString(policy, "x_oss_object_acl", "private"));
        await WriteFieldAsync("x-oss-forbid-overwrite", GetPolicyString(policy, "x_oss_forbid_overwrite", "true"));
        await WriteFieldAsync("key", key);
        await WriteFieldAsync("success_action_status", "200");

        var fileHead = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\nContent-Type: application/octet-stream\r\n\r\n";
        await bodyStream.WriteAsync(Encoding.UTF8.GetBytes(fileHead));
        await bodyStream.WriteAsync(audioBytes);
        await bodyStream.WriteAsync(Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n"));

        using var uploadContent = new ByteArrayContent(bodyStream.ToArray());
        uploadContent.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        logger?.LogInformation(
            "上传音频到临时存储：{Host} 大小 {Size:F1} MB",
            uploadHost,
            audioBytes.Length / 1024d / 1024d);
        using var uploadResponse = await Client.PostAsync(uploadHost, uploadContent);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            // OSS 的 400 会带 XML（Code/Message），不看正文根本不知道哪条策略没过.
            var body = await uploadResponse.Content.ReadAsStringAsync();
            logger?.LogWarning(
                "上传音频到临时存储失败：{Status} {Body}",
                (int)uploadResponse.StatusCode,
                body.Length > 600 ? body[..600] : body);
            return null;
        }

        return $"oss://{key}";
    }

    private static string GetPolicyString(JsonElement policy, string name, string fallback)
        => policy.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? fallback
            : fallback;

    private static async Task<string?> TranscribeAsync(string fileUrl, string apiKey, string model, ILogger? logger)
    {
        var submitPayload = BuildTranscriptionPayload(model, fileUrl);

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{DashScopeBase}/api/v1/services/audio/asr/transcription");
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        submitRequest.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");

        // 用上传拿到的 oss:// 临时文件时，DashScope 必须靠这个头才会按临时存储去取，
        // 不带就是 SERVER_ERROR / FILE_403（实测）。
        submitRequest.Headers.TryAddWithoutValidation("X-DashScope-OssResourceResolve", "enable");
        submitRequest.Content = new StringContent(submitPayload, Encoding.UTF8, "application/json");

        using var submitResponse = await Client.SendAsync(submitRequest);
        var submitBody = await submitResponse.Content.ReadAsStringAsync();
        if (!submitResponse.IsSuccessStatusCode)
        {
            logger?.LogWarning("提交音频转写任务失败：{Status} {Body}", (int)submitResponse.StatusCode, submitBody.Length > 400 ? submitBody[..400] : submitBody);
            return null;
        }

        using var submitDocument = JsonDocument.Parse(submitBody);
        if (!submitDocument.RootElement.TryGetProperty("output", out var output)
            || !output.TryGetProperty("task_id", out var taskIdNode))
        {
            // 2xx 但没 task_id：DashScope 会把原因写在 body 里（code / message / request_id）.
            logger?.LogWarning("提交音频转写任务没有返回 task_id：{Body}", submitBody.Length > 400 ? submitBody[..400] : submitBody);
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
                logger?.LogWarning(
                    "音频转写任务失败：{Body}",
                    statusDocument.RootElement.GetRawText() is { Length: > 600 } raw ? raw[..600] : statusDocument.RootElement.GetRawText());
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
                logger?.LogWarning(
                    "音频转写任务成功但没有 results：{Body}",
                    statusDocument.RootElement.GetRawText() is { Length: > 600 } raw ? raw[..600] : statusDocument.RootElement.GetRawText());
                return null;
            }

            var transcriptionUrl = results[0].TryGetProperty("transcription_url", out var urlNode)
                ? urlNode.GetString()
                : null;
            if (string.IsNullOrEmpty(transcriptionUrl))
            {
                logger?.LogWarning("音频转写结果里没有 transcription_url：{Body}", results[0].GetRawText());
                return null;
            }

            using var transcriptResponse = await Client.GetAsync(transcriptionUrl);
            if (!transcriptResponse.IsSuccessStatusCode)
            {
                logger?.LogWarning("下载转写结果失败：{Status}", (int)transcriptResponse.StatusCode);
                return null;
            }

            var transcriptBody = await transcriptResponse.Content.ReadAsStringAsync();
            var parsed = ParseTranscript(transcriptBody);
            if (string.IsNullOrWhiteSpace(parsed))
            {
                logger?.LogWarning(
                    "转写结果解析为空：{Body}",
                    transcriptBody.Length > 400 ? transcriptBody[..400] : transcriptBody);
            }

            return parsed;
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

        // 明确要原始字节，别让 CDN 走压缩.
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>
    /// 把下载回来的响应体变成真正的 m4s 字节：
    /// 兜一下「还是被压过」的情况，并确认拿到的确实是媒体数据而不是错误页.
    /// </summary>
    private static async Task<byte[]?> ReadMediaBytesAsync(HttpResponseMessage response, ILogger? logger)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0)
        {
            logger?.LogWarning("音频下载回来是空内容");
            return null;
        }

        // 极少数节点无视 identity，仍然回 gzip，这里手工解开.
        if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            try
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();
                await gzip.CopyToAsync(output);
                bytes = output.ToArray();
                logger?.LogInformation("音频响应被 gzip 压过，已手工解压到 {Size} 字节", bytes.Length);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "音频响应声称是 gzip 但解不开");
                return null;
            }
        }

        // m4s/mp4 的第 4-8 字节是 "ftyp"（有的分段是 "styp"），不对就是错误页或被截断.
        var isMedia = bytes.Length > 12
            && ((bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
                || (bytes[4] == 's' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p'));
        if (!isMedia)
        {
            var head = Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length)));
            logger?.LogWarning(
                "音频下载回来的不是媒体数据（{Size} 字节，Content-Type={Type}，Content-Encoding={Encoding}，头部 {Head}），放弃这次音频识别",
                bytes.Length,
                response.Content.Headers.ContentType?.MediaType ?? "无",
                response.Content.Headers.ContentEncoding.Count > 0
                    ? string.Join(",", response.Content.Headers.ContentEncoding)
                    : "无",
                head);
            return null;
        }

        return bytes;
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
        // 请求体里既有 JSON 也有整段音频（m4s 二进制），所以关闭自动解压：
        // 之前开 DecompressionMethods.All 时，B 站 CDN 会回一个声称 Content-Encoding: gzip
        // 但内容其实是原始 m4s 的响应，.NET 解压时抛 "Decoder ran into invalid data."，
        // 音频识别就整个失败了（日志里反复出现的那条）.
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
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






