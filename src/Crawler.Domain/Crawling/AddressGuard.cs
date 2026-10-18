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

    /// <summary>An IP literal as it appears in a URL host, brackets and all, or null when the host is a name.</summary>
    public static IPAddress? TryParseIpLiteral(string host) => IPAddress.TryParse(NormalizeHost(host).Trim('[', ']'), out var ip) ? ip : null;

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

        if (TryParseIpLiteral(host) is { } ip)
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
            var v6 = ip.GetAddressBytes();
            if (v6[0] == 0xff)
            {
                return true; // multicast ff00::/8
            }

            // Transition addresses carry an IPv4 address inside; judge that one.
            if (v6[0] == 0x20 && v6[1] == 0x02)
            {
                return IsPrivateV4([v6[2], v6[3], v6[4], v6[5]]); // 6to4 2002:AABB:CCDD::/16
            }

            if (v6[0] == 0 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b && v6.Take(12).Skip(4).All(x => x == 0))
            {
                return IsPrivateV4([v6[12], v6[13], v6[14], v6[15]]); // NAT64 64:ff9b::/96
            }

            if (v6.Take(12).All(x => x == 0))
            {
                return IsPrivateV4([v6[12], v6[13], v6[14], v6[15]]); // IPv4-compatible ::a.b.c.d
            }

            return false; // a global IPv6 address
        }

        return IsPrivateV4(ip.MapToIPv4().GetAddressBytes());
    }

    private static bool IsPrivateV4(byte[] b) =>
        b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)        // 192.0.0.0/24 IETF protocol assignments
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19))    // 198.18.0.0/15 benchmarking
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] >= 224                                    // multicast 224/4 and reserved 240/4 incl. broadcast
            || b[0] == 0
            || b[0] == 127;


    /// <summary>
    /// Resolve a host and return only its public addresses. Throws <see cref="AddressRefusedException"/> when the
    /// name does not resolve (NotFound) or when every address it resolves to is private (Private), so the two
    /// cases are never confused. The HTTP connect step uses this so the address actually dialled is vetted.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var normalized = NormalizeHost(host);
        IPAddress[] addresses;
        if (TryParseIpLiteral(normalized) is { } literal)
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
                throw new AddressRefusedException(normalized, AddressRefusal.NotFound);
            }

            if (addresses.Length == 0)
            {
                throw new AddressRefusedException(normalized, AddressRefusal.NotFound);
            }
        }

        var allowed = addresses.Where(a => !IsPrivate(a)).ToList();
        if (allowed.Count == 0)
        {
            throw new AddressRefusedException(normalized, AddressRefusal.Private);
        }

        return allowed;
    }
}
