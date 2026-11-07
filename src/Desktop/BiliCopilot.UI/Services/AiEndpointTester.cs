// Copyright (c) Bili Copilot. All rights reserved.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using BiliCopilot.UI.Services.Live;

namespace BiliCopilot.UI.Services;

/// <summary>
/// AI 接口连通性与模型可用性检查.
/// </summary>
internal static class AiEndpointTester
{
    private const string DashScopeBase = "https://dashscope.aliyuncs.com";

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = IpV4Connector.ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// 检查接口是否可达、密钥是否被接受.
    /// </summary>
    /// <param name="endpoint">接口地址.</param>
    /// <param name="key">密钥.</param>
    public static async Task<(bool Ok, string Message)> TestApiAsync(string endpoint, string key)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return (false, "接口地址还没填");
        }

        var baseUrl = endpoint.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/chat/completions".Length];
        }

        var url = $"{baseUrl}/models";
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            watch.Stop();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var modelCount = CountModels(body);
                return (true, modelCount > 0
                    ? $"接口可用（{watch.ElapsedMilliseconds} ms），返回 {modelCount} 个模型"
                    : $"接口可用（{watch.ElapsedMilliseconds} ms）");
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    => (false, $"密钥被拒绝（HTTP {(int)response.StatusCode}）"),
                HttpStatusCode.NotFound
                    => (false, "接口没有 /models 端点，可以直接测模型"),
                _ => (false, $"HTTP {(int)response.StatusCode}：{Shorten(body)}"),
            };
        }
        catch (Exception ex)
        {
            watch.Stop();
            return (false, $"连不上：{ex.Message}");
        }
    }

    /// <summary>
    /// 发一句最小请求，确认模型真的能回话.
    /// </summary>
    /// <param name="endpoint">接口地址.</param>
    /// <param name="key">密钥.</param>
    /// <param name="model">模型名.</param>
    public static async Task<(bool Ok, string Message)> TestModelAsync(string endpoint, string key, string model)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return (false, "接口地址还没填");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return (false, "模型名还没填");
        }

        var url = endpoint.Trim().TrimEnd('/');
        if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            url += "/chat/completions";
        }

        var payload = "{\"model\":\"" + Escape(model)
            + "\",\"messages\":[{\"role\":\"user\",\"content\":\"只回复两个字：正常\"}],\"max_tokens\":32,\"temperature\":0}";

        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            watch.Stop();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode}：{Shorten(body)}");
            }

            var content = ReadReply(body);
            return string.IsNullOrWhiteSpace(content)
                ? (false, "模型返回内容为空")
                : (true, $"模型可用（{watch.ElapsedMilliseconds} ms）：{Shorten(content, 48)}");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return (false, $"请求失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 检查音频分析（阿里云 DashScope 转写）的 Key 与转写模型是否被接受.
    /// </summary>
    /// <remarks>
    /// 走的是转写流程的第一步 getPolicy：这一步能同时验出 Key 是否有效、模型名是否被认可，
    /// 又不会真的上传音频、也不会消耗转写额度.
    /// </remarks>
    /// <param name="key">阿里云 DashScope Key.</param>
    /// <param name="model">转写模型，留空按 paraformer-v2 处理.</param>
    public static async Task<(bool Ok, string Message)> TestAudioAnalysisAsync(string key, string model)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return (false, "阿里云 DashScope Key 还没填，音频分析会被直接跳过");
        }

        var useModel = string.IsNullOrWhiteSpace(model) ? "paraformer-v2" : model.Trim();
        var url = $"{DashScopeBase}/api/v1/uploads?action=getPolicy&model={WebUtility.UrlEncode(useModel)}";
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            watch.Stop();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return body.Contains("upload_host", StringComparison.Ordinal)
                    ? (true, $"Key 与模型 {useModel} 都被接受（{watch.ElapsedMilliseconds} ms）")
                    : (true, $"接口通了（{watch.ElapsedMilliseconds} ms），但没返回上传策略：{Shorten(body, 80)}");
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    => (false, $"密钥被拒绝（HTTP {(int)response.StatusCode}）"),
                HttpStatusCode.NotFound
                    => (false, "接口路径不对（HTTP 404），检查网络或代理"),
                _ => (false, $"HTTP {(int)response.StatusCode}：{Shorten(body)}"),
            };
        }
        catch (Exception ex)
        {
            watch.Stop();
            return (false, $"连不上：{ex.Message}");
        }
    }

    /// <summary>
    /// 拉取接口声明的可用模型列表.
    /// </summary>
    /// <param name="endpoint">接口地址.</param>
    /// <param name="key">密钥.</param>
    public static async Task<(bool Ok, string Message, List<string> Models)> FetchModelsAsync(string endpoint, string key)
    {
        var models = new List<string>();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return (false, "接口地址还没填", models);
        }

        var baseUrl = endpoint.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/chat/completions".Length];
        }

        var url = $"{baseUrl}/models";
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            }

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            watch.Stop();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "密钥被拒绝",
                    HttpStatusCode.NotFound => "接口没有 /models 端点，只能手动填模型名",
                    _ => $"HTTP {(int)response.StatusCode}：{Shorten(body)}",
                };
                return (false, reason, models);
            }

            models.AddRange(ParseModelIds(body));
            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models.Count > 0
                ? (true, $"查到 {models.Count} 个模型（{watch.ElapsedMilliseconds} ms）", models)
                : (true, "接口能通，但没解析到模型列表，手动填吧", models);
        }
        catch (Exception ex)
        {
            watch.Stop();
            return (false, $"请求失败：{ex.Message}", models);
        }
    }

    /// <summary>
    /// 从 /models 的返回里抠出模型名，兼容 data[].id 和 models[].name 两种写法.
    /// </summary>
    private static IEnumerable<string> ParseModelIds(string body)
    {
        var result = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = ReadString(item, "id") ?? ReadString(item, "name");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result.Add(id);
                    }
                }
            }
            else if (document.RootElement.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var name = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : ReadString(item, "name") ?? ReadString(item, "id");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.Add(name);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 返回不是 JSON，交给上层提示.
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadReply(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var message = choices[0].GetProperty("message");
            if (message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(content.GetString()))
            {
                return content.GetString()!;
            }

            return message.TryGetProperty("reasoning_content", out var reasoning)
                && reasoning.ValueKind == JsonValueKind.String
                ? reasoning.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static int CountModels(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string Shorten(string value, int max = 120)
    {
        var text = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
}