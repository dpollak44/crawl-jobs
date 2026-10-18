namespace Crawler.Domain.Crawling;

public sealed record LinkCounts(int Internal, int Total)
{
    /// <summary>Internal / Total; 0 when the page has no outgoing links. Rounded to 4 places for storage.</summary>
    public decimal Ratio => Total == 0 ? 0m : Math.Round((decimal)Internal / Total, 4, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Domain Link Ratio = links that stay on the starting site / all outgoing links.
/// Expects the links already normalized and de-duplicated by <see cref="LinkExtractor"/>, so a link
/// repeated five times on a page counts once.
/// </summary>
public static class DomainLinkRatio
{
    public static LinkCounts Count(IReadOnlyCollection<Uri> outgoingLinks, SiteScope scope)
    {
        var internalCount = outgoingLinks.Count(scope.Contains);
        return new LinkCounts(internalCount, outgoingLinks.Count);
    }
}
