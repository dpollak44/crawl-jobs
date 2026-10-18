using AngleSharp.Html.Parser;

namespace Crawler.Domain.Crawling;

/// <summary>
/// Pulls the outgoing links out of an HTML page: every &lt;a href&gt;, normalized, de-duplicated, and
/// with links to the page itself removed. Only anchors count as "links": scripts, stylesheets and images
/// are resources the page loads, not places it sends a reader.
/// </summary>
public static class LinkExtractor
{
    private static readonly HtmlParser Parser = new();

    public static IReadOnlyList<Uri> Extract(string html, Uri pageUrl)
    {
        var document = Parser.ParseDocument(html);
        var self = UrlNormalizer.TryNormalize(pageUrl.ToString(), pageUrl, out var normalizedSelf) ? normalizedSelf : pageUrl;

        // <base href> changes how relative links resolve; honour it like a browser would.
        var baseUri = self;
        var baseHref = document.QuerySelector("base[href]")?.GetAttribute("href");
        if (baseHref is not null && Uri.TryCreate(self, baseHref, out var declaredBase))
        {
            baseUri = declaredBase;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<Uri>();
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (!UrlNormalizer.TryNormalize(anchor.GetAttribute("href"), baseUri, out var target))
            {
                continue;
            }

            var key = target.ToString();
            if (key == self.ToString() || !seen.Add(key))
            {
                continue;
            }

            links.Add(target);
        }

        return links;
    }
}
