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

    /// <summary>How often the worker looks for pages left Queued with no message in flight.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>A Queued page older than this on a running job is assumed to have lost its message and is re-published.</summary>
    public int StuckAfterSeconds { get; set; } = 120;
}
