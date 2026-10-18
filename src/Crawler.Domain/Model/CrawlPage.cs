namespace Crawler.Domain.Model;

public enum PageStatus
{
    Queued,
    Fetched,
    Failed,
    Skipped,
}

/// <summary>
/// One page within one job. Written at discovery time with status Queued; the unique key (JobId, Url) is
/// what makes a duplicate delivery harmless. ParentPageId and Depth record the FIRST discovery, so the tree
/// view has exactly one shape even when pages link back to earlier pages.
/// </summary>
public sealed class CrawlPage
{
    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public string Url { get; private set; }
    public int Depth { get; private set; }
    public Guid? ParentPageId { get; private set; }
    public PageStatus Status { get; private set; }
    public string? FinalUrl { get; private set; }
    public int? HttpStatus { get; private set; }
    public int OutgoingLinks { get; private set; }
    public int InternalLinks { get; private set; }
    public decimal DomainLinkRatio { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset DiscoveredAt { get; private set; }

    /// <summary>When a message for this page was last put on the queue. The sweeper judges "stuck" by this, not by discovery time.</summary>
    public DateTimeOffset LastQueuedAt { get; private set; }
    public DateTimeOffset? FetchedAt { get; private set; }

    private CrawlPage()
    {
        Url = string.Empty;
    }

    public static CrawlPage Discover(Guid jobId, Uri normalizedUrl, int depth, Guid? parentPageId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        JobId = jobId,
        Url = normalizedUrl.ToString(),
        Depth = depth,
        ParentPageId = parentPageId,
        Status = PageStatus.Queued,
        DiscoveredAt = now,
        LastQueuedAt = now,
    };

    public void MarkFetched(Uri finalUrl, int httpStatus, int outgoingLinks, int internalLinks, decimal ratio, DateTimeOffset now)
    {
        Status = PageStatus.Fetched;
        FinalUrl = finalUrl.ToString();
        HttpStatus = httpStatus;
        OutgoingLinks = outgoingLinks;
        InternalLinks = internalLinks;
        DomainLinkRatio = ratio;
        FetchedAt = now;
    }

    public void MarkFailed(string error, int? httpStatus, DateTimeOffset now)
    {
        Status = PageStatus.Failed;
        Error = error;
        HttpStatus = httpStatus;
        FetchedAt = now;
    }

    /// <summary>The URL answered but is not an HTML page (PDF, image, redirect off-site). Not an error; nothing to parse.</summary>
    public void MarkSkipped(string reason, int? httpStatus, DateTimeOffset now)
    {
        Status = PageStatus.Skipped;
        Error = reason;
        HttpStatus = httpStatus;
        FetchedAt = now;
    }
}
