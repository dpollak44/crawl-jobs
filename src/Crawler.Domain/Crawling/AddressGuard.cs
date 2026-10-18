using System.Net;

namespace Crawler.Domain.Crawling;

/// <summary>
/// A crawler must never be talked into fetching something inside the network it runs on.
/// Two layers: by NAME before any request (localhost, .local, .internal, literal private IPs), and by RESOLVED
/// ADDRESS at connection time (<see cref="ResolveAllowedAsync"/>), so a public-looking name that resolves to an
/// internal address is refused and cannot be rebound mid-crawl.
/// </summary>
public static class AddressGuard
{
    public static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    public static bool IsPublicWebAddress(Uri url)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = NormalizeHost(url.Host);
        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal))
        {
            return false;
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return !IsPrivate(ip);
        }

        return host.Contains('.');
    }

    /// <summary>Loopback, link-local, private ranges (10/8, 172.16/12, 192.168/16), carrier-grade NAT (100.64/10), unspecified, IPv6 local scopes.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !ip.IsIPv4MappedToIPv6)
        {
            return false; // a global IPv6 address
        }

        var b = ip.MapToIPv4().GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] == 0
            || b[0] == 127;
    }

    /// <summary>
    /// Resolve a host and return only its public addresses, or an empty list when every address is private
    /// (or the name does not resolve). The HTTP connect step uses this so the address actually dialled is vetted.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var normalized = NormalizeHost(host);
        IPAddress[] addresses;
        if (IPAddress.TryParse(normalized.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolve(normalized, ct);
            }
            catch (System.Net.Sockets.SocketException)
            {
                return [];
            }
        }

        return addresses.Where(a => !IsPrivate(a)).ToList();
    }
}
