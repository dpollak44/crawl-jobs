using System.Net;

namespace Crawler.Domain.Crawling;

/// <summary>
/// A crawler must never be talked into fetching something inside the network it runs on. Rejects loopback,
/// link-local, private-range and literal-IP targets and "localhost". Names that resolve to private addresses
/// are a DNS-level concern (next step); this stops the obvious cases, including redirects.
/// </summary>
public static class AddressGuard
{
    public static bool IsPublicWebAddress(Uri url)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = url.Host.ToLowerInvariant();
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

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
        {
            return true;
        }

        var b = ip.MapToIPv4().GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || b[0] == 0
            || b[0] == 127;
    }
}
