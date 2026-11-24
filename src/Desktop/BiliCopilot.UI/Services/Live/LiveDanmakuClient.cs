// Copyright (c) Bili Copilot. All rights reserved.

using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// B 站直播消息客户端.
/// 直接连 broadcastlv 弹幕服务器：认证包、心跳包、brotli 解压、逐包解析，
/// 参考 BiliLite 的 LiveMessage 实现，尽量保留原始字段以满足互动面板的展示需要.
/// </summary>
internal sealed class LiveDanmakuClient : IAsyncDisposable
{
    private const int HeaderLength = 16;
    private const int OperationHeartbeat = 2;
    private const int OperationMessage = 5;
    private const int OperationAuth = 7;
    private const int OperationAuthReply = 8;

    private readonly HttpClient _httpClient = new(new SocketsHttpHandler { ConnectCallback = IpV4Connector.ConnectAsync }) { Timeout = TimeSpan.FromSeconds(15) };
    private int _loggedPackets;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;

    /// <summary>
    /// 收到消息（cmd 与解析后的 JSON）.
    /// <para>
    /// <c>Data</c> 只在回调期间有效：回调返回后底层的 <see cref="JsonDocument"/> 就会被释放，
    /// 需要留存请自己在回调里 <c>Clone()</c>。这样每条消息不用再复制一整份 JSON 字节
    /// （密集直播间每收一条弹幕就是一次全量拷贝 + 一个数组分配）.
    /// </para>
    /// </summary>
    public event EventHandler<(string Command, JsonElement Data)>? MessageReceived;

    /// <summary>
    /// 连接状态变化.
    /// </summary>
    public event EventHandler<string>? StateChanged;

    /// <summary>
    /// 当前是否已连接.
    /// </summary>
    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <summary>
    /// 连接到直播间弹幕服务器.
    /// </summary>
    /// <param name="roomId">长房间号.</param>
    /// <param name="cookie">登录 Cookie（可为空，游客也能收弹幕）.</param>
    /// <param name="uid">当前用户 ID.</param>
    /// <param name="logger">日志.</param>
    public async Task ConnectAsync(string roomId, string cookie, long uid, ILogger? logger = null)
    {
        await StopAsync().ConfigureAwait(false);

        logger?.LogInformation("直播弹幕：开始连接房间 {Room}", roomId);
        var info = await GetDanmakuServerAsync(roomId, cookie, logger).ConfigureAwait(false);
        if (info is null)
        {
            StateChanged?.Invoke(this, "获取弹幕服务器失败");
            return;
        }

        var (token, host) = info.Value;
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
        socket.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            socket.Options.SetRequestHeader("Cookie", cookie);
        }

        var uri = new Uri($"wss://{host}/sub");
        using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
        }

        logger?.LogInformation("直播弹幕：已连上 {Host}", host);
        _socket = socket;

        // 裁剪下 JsonSerializer 不可用，手工拼接认证与发弹幕的 JSON.
        var authPayload = $"{{\"uid\":{uid},\"roomid\":{(long.TryParse(roomId, out var rid) ? rid : 0)},\"protover\":3,\"platform\":\"web\",\"type\":2,\"key\":\"{token}\"}}";
        await SendAsync(socket, OperationAuth, Encoding.UTF8.GetBytes(authPayload)).ConfigureAwait(false);

        _cts = new CancellationTokenSource();

        // 直接起 async 方法即可：两个循环上来就 await，不需要再包一层 Task.Run 跳线程池.
        _receiveTask = ReceiveLoopAsync(socket, _cts.Token, logger);
        _heartbeatTask = HeartbeatLoopAsync(socket, _cts.Token, logger);
        StateChanged?.Invoke(this, "弹幕已连接");
    }

    /// <summary>
    /// 发送弹幕（走直播间 WS 的 SEND_MSG 包）.
    /// </summary>
    public async Task<bool> SendDanmakuAsync(string roomId, string text)
    {
        if (_socket is not { State: WebSocketState.Open })
        {
            return false;
        }

        var payload = $"{{\"color\":16777215,\"fontsize\":25,\"mode\":1,\"msg\":\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\",\"rnd\":{DateTimeOffset.Now.ToUnixTimeSeconds()},\"roomid\":{(long.TryParse(roomId, out var rid) ? rid : 0)},\"biz\":0}}";
        await SendAsync(_socket, 5, Encoding.UTF8.GetBytes(payload)).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }

    private async Task StopAsync()
    {
        try
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex;
            }
        }
        catch (Exception ex)
        {
            _ = ex;
        }

        if (_socket is { State: WebSocketState.Open })
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }

        _socket?.Dispose();
        _socket = null;
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
        _heartbeatTask = null;
    }

    private async Task<(string Token, string Host)?> GetDanmakuServerAsync(string roomId, string cookie, ILogger? logger)
    {
        try
        {
            // 2025-06 起该接口要求 buvid3 + WBI 签名，否则返回 -352.
            var buvid3 = await BiliWbiSigner.GetBuvid3Async(cookie, logger).ConfigureAwait(false);
            var query = await BiliWbiSigner.SignAsync(
                new Dictionary<string, string> { ["id"] = roomId },
                cookie,
                logger).ConfigureAwait(false);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?{query}");
            request.Headers.TryAddWithoutValidation("Referer", $"https://live.bilibili.com/{roomId}");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            var cookieHeader = string.IsNullOrWhiteSpace(buvid3) ? cookie : $"{cookie}; buvid3={buvid3}";
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("获取弹幕服务器失败：HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            logger?.LogInformation("弹幕服务器响应长度 {Length}", body.Length);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("code", out var codeNode) && codeNode.TryGetInt32(out var code) && code != 0)
            {
                logger?.LogWarning("获取弹幕服务器失败：code={Code}", code);
                return null;
            }

            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            var token = data.TryGetProperty("token", out var tokenNode) ? tokenNode.GetString() ?? string.Empty : string.Empty;
            logger?.LogInformation("直播弹幕：拿到 token，长度 {Length}", token.Length);
            var host = "broadcastlv.chat.bilibili.com";
            if (data.TryGetProperty("host_list", out var hosts)
                && hosts.ValueKind == JsonValueKind.Array
                && hosts.GetArrayLength() > 0
                && hosts[0].TryGetProperty("host", out var hostNode))
            {
                host = hostNode.GetString() ?? host;
            }

            return (token, host);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "获取直播弹幕服务器信息失败");
            return null;
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken token, ILogger? logger)
    {
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                await SendAsync(socket, OperationHeartbeat, Encoding.UTF8.GetBytes("[object Object]")).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "直播心跳发送失败");
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token, ILogger? logger)
    {
        var buffer = new byte[64 * 1024];

        // 消息缓冲跨包复用：原来每条消息 ToArray() 一整块，等于每收一条弹幕就多一次分配+拷贝.
        var message = new byte[256 * 1024];
        var filled = 0;
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (filled + result.Count > message.Length)
                {
                    Array.Resize(ref message, Math.Max(message.Length * 2, filled + result.Count));
                }

                buffer.AsSpan(0, result.Count).CopyTo(message.AsSpan(filled));
                filled += result.Count;
                if (!result.EndOfMessage)
                {
                    continue;
                }

                // 切片视图交给解析，解析完（await 返回）才会再次写入 message，不会踩数据.
                await ParsePacketsAsync(message.AsMemory(0, filled), logger).ConfigureAwait(false);
                filled = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "直播弹幕接收结束");
            StateChanged?.Invoke(this, "弹幕连接断开");
        }
    }

    private async Task ParsePacketsAsync(ReadOnlyMemory<byte> data, ILogger? logger)
    {
        var offset = 0;
        while (offset + HeaderLength <= data.Length)
        {
            var packetLength = ReadInt32(data, offset);
            var headerLength = ReadUInt16(data, offset + 4);
            var protoVersion = ReadUInt16(data, offset + 6);
            var operation = ReadInt32(data, offset + 8);
            if (packetLength <= 0 || offset + packetLength > data.Length)
            {
                break;
            }

            var bodyLength = packetLength - headerLength;
            if (bodyLength < 0)
            {
                break;
            }

            // 包体用切片视图，不再每包 new byte[] + BlockCopy.
            var body = data.Slice(offset + headerLength, bodyLength);
            offset += packetLength;

            if (_loggedPackets++ < 5)
            {
                logger?.LogInformation("弹幕包：op={Op} proto={Proto} len={Len}", operation, protoVersion, packetLength);
            }

            if (operation == OperationAuthReply)
            {
                StateChanged?.Invoke(this, "弹幕认证成功");
                continue;
            }

            if (operation != OperationMessage)
            {
                continue;
            }

            // protover 2/3 是压缩包，里面还套着多个包.
            if (protoVersion is 2 or 3)
            {
                try
                {
                    var decompressed = await DecompressAsync(body, protoVersion == 3).ConfigureAwait(false);
                    if (decompressed is not null)
                    {
                        var (payload, length) = decompressed.Value;

                        // 解压缓冲是新分配的，解析完就可以丢，不受 message 复用影响.
                        await ParsePacketsAsync(payload.AsMemory(0, length), logger).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "直播弹幕解压失败");
                }

                continue;
            }

            try
            {
                // 直接对 UTF-8 字节解析，省掉「解码成 string 再 Parse」的中间字符串.
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (!root.TryGetProperty("cmd", out var cmdNode))
                {
                    continue;
                }

                var command = cmdNode.GetString() ?? string.Empty;
                if (_loggedPackets < 12)
                {
                    logger?.LogInformation("弹幕消息：{Cmd}", command);
                }

                // 直接把切片上的 root 交出去：Clone() 会把整份 JSON 再复制一遍字节，
                // 而唯一的订阅方（PlayerViewModel）在回调里同步读完就丢。
                // 事件约定已写明「只在回调期间有效」.
                MessageReceived?.Invoke(this, (command, root));
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "直播弹幕 JSON 解析失败");
            }
        }
    }

    private static async Task<(byte[] Buffer, int Length)?> DecompressAsync(ReadOnlyMemory<byte> body, bool isBrotli)
    {
        // 只读视图直接喂给解压流，省掉包体的一次 ToArray 拷贝.
        using var input = new ReadOnlyMemoryStream(body);
        using var output = new MemoryStream();
        using (Stream decompressor = isBrotli ? new BrotliStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress))
        {
            await decompressor.CopyToAsync(output).ConfigureAwait(false);
        }

        if (output.Length == 0)
        {
            return null;
        }

        // GetBuffer() 拿内部缓冲区，省掉 ToArray 的第二次拷贝；调用方需要一起带上有效长度.
        return (output.GetBuffer(), (int)output.Length);
    }

    private static async Task SendAsync(ClientWebSocket socket, int operation, byte[] body)
    {
        var packet = new byte[HeaderLength + body.Length];
        WriteInt32(packet, 0, packet.Length);
        WriteUInt16(packet, 4, HeaderLength);
        WriteUInt16(packet, 6, 1);
        WriteInt32(packet, 8, operation);
        WriteInt32(packet, 12, 1);
        Buffer.BlockCopy(body, 0, packet, HeaderLength, body.Length);
        await socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
    }

    private static int ReadInt32(ReadOnlyMemory<byte> data, int offset)
    {
        var span = data.Span;
        return span[offset] << 24 | span[offset + 1] << 16 | span[offset + 2] << 8 | span[offset + 3];
    }

    private static int ReadUInt16(ReadOnlyMemory<byte> data, int offset)
    {
        var span = data.Span;
        return span[offset] << 8 | span[offset + 1];
    }

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteUInt16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    /// <summary>
    /// 只读、不可寻址的 <see cref="Stream"/> 视图，把包头里的包体直接喂给 Brotli/ZLib 解压流，
    /// 省掉每包一次 ToArray 拷贝.
    /// </summary>
    private sealed class ReadOnlyMemoryStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _buffer;
        private int _position;

        public ReadOnlyMemoryStream(ReadOnlyMemory<byte> buffer) => _buffer = buffer;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _buffer.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toCopy = Math.Min(count, remaining);
            _buffer.Slice(_position, toCopy).Span.CopyTo(buffer.AsSpan(offset, toCopy));
            _position += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = _buffer.Length - _position;
            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var toCopy = Math.Min(buffer.Length, remaining);
            _buffer.Slice(_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
