namespace Crawler.Domain.Crawling;

/// <summary>
/// Decides whether a URL is "inside the starting domain". The brief defines the domain as the host of the
/// job's URL, and that is applied exactly: `www.example.com` and `example.com` are two different hosts, so a
/// link from one to the other is an outside link. Scheme is ignored (http and https on the same host are the
/// same site). Subdomains (blog.example.com) are outside unless IncludeSubdomains is on. The same rule decides
/// both what counts in the ratio and what we fetch. Host comparison is case-insensitive with a trailing dot ignored.
/// </summary>
public sealed class SiteScope
{
    public string Host { get; }
    public bool IncludeSubdomains { get; }

    public SiteScope(string startHost, bool includeSubdomains = false)
    {
        Host = AddressGuard.NormalizeHost(startHost);
        IncludeSubdomains = includeSubdomains;
    }

    public bool Contains(Uri url)
    {
        var host = AddressGuard.NormalizeHost(url.Host);
        if (host == Host)
        {
            return true;
        }

        return IncludeSubdomains && host.EndsWith("." + Host, StringComparison.Ordinal);
    }

    /// <summary>
    /// One page, one key: a same-site link spelled with the other scheme is rewritten to the job's own scheme
    /// before it becomes a page row. The host is already exact by definition. Off-site links are returned unchanged.
    /// </summary>
    public Uri Canonicalize(Uri link, Uri seed)
    {
        if (!Contains(link) || link.Scheme == seed.Scheme)
        {
            return link;
        }

        return new UriBuilder(link) { Scheme = seed.Scheme, Port = link.IsDefaultPort ? -1 : link.Port }.Uri;
    }
}
