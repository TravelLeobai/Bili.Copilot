// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;
using System.Net.Sockets;

namespace BiliCopilot.UI.Services.Live;

/// <summary>
/// 只走 IPv4 的连接器.
/// 本机 IPv6 出口不通，.NET 默认会先试 AAAA 并挂到超时，直播相关请求统一强制 IPv4.
/// </summary>
internal static class IpV4Connector
{
    /// <summary>
    /// 供 <see cref="SocketsHttpHandler.ConnectCallback"/> 使用.
    /// </summary>
    /// <param name="context">连接上下文.</param>
    /// <param name="cancellationToken">取消令牌.</param>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var address = Array.Find(addresses, p => p.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new SocketException((int)SocketError.HostNotFound);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}