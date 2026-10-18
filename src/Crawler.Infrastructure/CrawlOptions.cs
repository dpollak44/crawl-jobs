namespace Crawler.Infrastructure;

/// <summary>Every number that governs a crawl, with its default. Per-job values are copied onto the job row at creation.</summary>
public sealed class CrawlOptions
{
    public const string Section = "Crawl";

    public int DefaultMaxDepth { get; set; } = 2;
    public int MaxDepthLimit { get; set; } = 5;
    public int DefaultMaxPages { get; set; } = 200;
    public int MaxPagesLimit { get; set; } = 1000;
    public bool IncludeSubdomains { get; set; }
    public int FetchTimeoutSeconds { get; set; } = 10;
    public int FetchRetries { get; set; } = 2;
    public int MaxRedirects { get; set; } = 5;
    public long MaxBodyBytes { get; set; } = 2 * 1024 * 1024;
}
