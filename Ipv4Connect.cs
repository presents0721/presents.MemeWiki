using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace presents.MemeWiki;

/// <summary>
/// 只连 IPv4，并且对所有 A 记录**并发**发起连接（谁先连上谁赢）。
///
/// 两个坑都踩过：
///   1. 国内不少网络的 IPv6 是黑洞：AAAA 能解析、连 0.01 秒就失败，
///      而 .NET 默认先试 IPv6，一路耗到超时。→ 只挑 IPv4。
///   2. Cloudflare 给的多条 A 记录里，经常有一条是死的（TCP 直接挂住）。
///      串行重试时，第一条卡住就把整个 ConnectTimeout 吃光，第二条根本没机会。
///      → 并发竞速，最快的那条胜出。
/// </summary>
internal static class Ipv4Connect
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        Uri uri = context.InitialRequestMessage.RequestUri
                  ?? throw new IOException("连接上下文缺少请求地址");

        string host = uri.Host;
        int port = uri.Port;

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token);

        IPAddress[] ipv4 = addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).ToArray();
        IPAddress[] candidates = ipv4.Length > 0 ? ipv4 : addresses;

        if (candidates.Length == 0)
            throw new IOException($"无法解析 {host}");

        using CancellationTokenSource race = CancellationTokenSource.CreateLinkedTokenSource(token);

        List<Task<Socket?>> attempts = candidates
            .Select(ip => TryConnectAsync(ip, port, race.Token))
            .ToList();

        while (attempts.Count > 0)
        {
            Task<Socket?> finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);

            Socket? socket = finished.Status == TaskStatus.RanToCompletion ? finished.Result : null;

            if (socket != null)
            {
                // 赢家定了：取消其它还挂着的连接，避免继续占着 socket
                try { race.Cancel(); } catch { }
                return new NetworkStream(socket, ownsSocket: true);
            }
        }

        throw new IOException($"无法连接 {host}:{port}（{candidates.Length} 个地址都失败）");
    }

    static async Task<Socket?> TryConnectAsync(IPAddress ip, int port, CancellationToken token)
    {
        Socket socket = new(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }
}
