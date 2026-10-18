namespace Crawler.Domain.Model;

/// <summary>
/// One outgoing link from a fetched page (the parent → child edge). Every link is recorded, including ones
/// that leave the site and are never fetched, because they are the evidence behind the page's ratio.
/// Unique on (JobId, ParentPageId, ChildUrl) so a re-processed page cannot double its edges.
/// </summary>
public sealed class PageLink
{
    public long Id { get; private set; }
    public Guid JobId { get; private set; }
    public Guid ParentPageId { get; private set; }
    public string ChildUrl { get; private set; }
    public bool IsInternal { get; private set; }

    private PageLink()
    {
        ChildUrl = string.Empty;
    }

    public static PageLink Create(Guid jobId, Guid parentPageId, Uri childUrl, bool isInternal) => new()
    {
        JobId = jobId,
        ParentPageId = parentPageId,
        ChildUrl = childUrl.ToString(),
        IsInternal = isInternal,
    };
}
