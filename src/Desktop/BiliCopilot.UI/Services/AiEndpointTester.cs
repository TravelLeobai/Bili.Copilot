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