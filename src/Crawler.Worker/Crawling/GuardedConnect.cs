using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Crawler.Domain.Crawling;

namespace Crawler.Worker.Crawling;

/// <summary>
/// The HTTP client's connect step: resolve the host ourselves, refuse any private result, and dial only a vetted
/// address. This closes the gap the name check cannot: a public-looking name that resolves to 10.x, or is
/// rebound to one between requests.
/// </summary>
public static class GuardedConnect
{
    public static async ValueTask<System.IO.Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var allowed = await AddressGuard.ResolveAllowedAsync(host, (h, token) => Dns.GetHostAddressesAsync(h, token), ct);
        if (allowed.Count == 0)
        {
            throw new HttpRequestException($"Refusing to connect to {host}: it resolves only to private or internal addresses.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed.ToArray(), context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
