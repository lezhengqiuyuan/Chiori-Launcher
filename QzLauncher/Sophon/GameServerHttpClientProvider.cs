/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FufuLauncher.Services.GameServer;

public sealed class GameServerHttpClientProvider
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36";

    /// <summary>
    /// 全局优选 CDN 节点 IP（用于无感内存直连加速，无需依赖 Hosts）
    /// </summary>
    public static string? OptimalCdnIp { get; set; }

    private HttpClient? _apiClient;
    private HttpClient? _chunkClient;
    
    public HttpClient ApiClient => _apiClient ??= CreateApiClientCore();
    
    public HttpClient ChunkClient => _chunkClient ??= CreateChunkClientCore();

    public void ResetClient()
    {
        _chunkClient?.Dispose();
        _chunkClient = null;
    }

    private static HttpClient CreateApiClientCore()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = OnConnectAsync
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    private static HttpClient CreateChunkClientCore()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 64, // 突破8连接瓶颈，大幅释放并发下载吞吐
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectCallback = OnConnectAsync
        };

        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    private static async ValueTask<Stream> OnConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var targetHost = context.DnsEndPoint.Host;
        var targetPort = context.DnsEndPoint.Port;

        // 如果配置了最优 IP 且请求的是海外 CDN 域名，强制直连最优香港/亚太 IP
        if (!string.IsNullOrEmpty(OptimalCdnIp) && 
            string.Equals(targetHost, "autopatchhk.yuanshen.com", StringComparison.OrdinalIgnoreCase))
        {
            var cdnSocket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await cdnSocket.ConnectAsync(OptimalCdnIp, targetPort, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(cdnSocket, ownsSocket: true);
            }
            catch
            {
                cdnSocket.Dispose();
                // 降级回默认 DNS 解析
            }
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
