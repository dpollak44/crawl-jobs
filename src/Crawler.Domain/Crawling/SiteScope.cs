namespace Crawler.Domain.Crawling;

/// <summary>
/// Decides whether a URL is "inside the starting domain". The brief defines the domain as the host of the
/// job's URL, so by default that is an exact host match, with one concession to reality: a leading "www."
/// is ignored, because example.com and www.example.com are the same site everywhere. Subdomains
/// (blog.example.com) are outside unless IncludeSubdomains is on. Scheme is ignored: http and https on the
/// same host are the same site. The same rule decides both what counts in the ratio and what we fetch.
/// </summary>
public sealed class SiteScope
{
    public string Host { get; }
    public bool IncludeSubdomains { get; }

    public SiteScope(string startHost, bool includeSubdomains = false)
    {
        Host = Strip(startHost);
        IncludeSubdomains = includeSubdomains;
    }

    public bool Contains(Uri url)
    {
        var host = Strip(url.Host);
        if (host == Host)
        {
            return true;
        }

        return IncludeSubdomains && host.EndsWith("." + Host, StringComparison.Ordinal);
    }

    /// <summary>
    /// One page, one key: a same-site link spelled with the other scheme or with/without "www." is rewritten
    /// to the job's own scheme and host before it becomes a page row. Off-site links are returned unchanged.
    /// </summary>
    public Uri Canonicalize(Uri link, Uri seed)
    {
        if (!Contains(link))
        {
            return link;
        }

        if (link.Scheme == seed.Scheme && string.Equals(link.Host, seed.Host, StringComparison.OrdinalIgnoreCase))
        {
            return link;
        }

        return new UriBuilder(link) { Scheme = seed.Scheme, Host = seed.Host, Port = link.IsDefaultPort ? -1 : link.Port }.Uri;
    }

    private static string Strip(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }
}
