namespace Crawler.Domain.Crawling;

/// <summary>
/// Turns whatever appears in an href into one canonical absolute URL, or rejects it.
/// Two spellings of the same page must normalize to the same string, because that string is the
/// idempotency key for the pages table.
///
/// Rules: resolve relative to the page it was found on; http(s) only; drop the fragment; lowercase scheme
/// and host; drop a default port; strip a trailing slash except on the root; keep path case and the query
/// string exactly as written (both can be significant).
/// </summary>
public static class UrlNormalizer
{
    public static bool TryNormalize(string? href, Uri baseUri, out Uri normalized)
    {
        normalized = null!;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var trimmed = href.Trim();

        // Non-web schemes (mailto:, tel:, javascript:, data:, ftp:) are never links we follow or count.
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && trimmed.AsSpan(0, colon).IndexOfAny('/', '?', '#') < 0)
        {
            var scheme = trimmed[..colon].ToLowerInvariant();
            if (scheme is not ("http" or "https"))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(baseUri, trimmed, out var absolute) || !absolute.IsAbsoluteUri)
        {
            return false;
        }

        if (absolute.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var builder = new UriBuilder(absolute)
        {
            Scheme = absolute.Scheme.ToLowerInvariant(),
            Host = absolute.Host.ToLowerInvariant(),
            Fragment = string.Empty,
        };

        if (builder.Port == (builder.Scheme == "https" ? 443 : 80))
        {
            builder.Port = -1; // default port: omit it
        }

        if (builder.Path.Length > 1 && builder.Path.EndsWith('/'))
        {
            builder.Path = builder.Path.TrimEnd('/');
        }

        normalized = builder.Uri;
        return true;
    }
}
