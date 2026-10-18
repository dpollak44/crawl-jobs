namespace Crawler.Domain.Model;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// One crawl request. Owns its own limits (depth, page cap, start host) so a later change of defaults never
/// changes the meaning of an old job. PagesDiscovered is the cap counter; it is raised only by an atomic,
/// conditional UPDATE in the worker, which is what stops several workers from slipping past the cap together.
/// </summary>
public sealed class CrawlJob
{
    public Guid Id { get; private set; }
    public string InputUrl { get; private set; }
    public string StartHost { get; private set; }
    public int MaxDepth { get; private set; }
    public int MaxPages { get; private set; }
    public JobStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public int PagesDiscovered { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private CrawlJob()
    {
        InputUrl = string.Empty;
        StartHost = string.Empty;
    }

    public static CrawlJob Create(Uri startUrl, int maxDepth, int maxPages, DateTimeOffset now)
    {
        if (maxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Depth cannot be negative.");
        }

        if (maxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPages), "Page cap must be at least 1.");
        }

        return new CrawlJob
        {
            Id = Guid.NewGuid(),
            InputUrl = startUrl.ToString(),
            StartHost = startUrl.Host.ToLowerInvariant(),
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Status = JobStatus.Pending,
            PagesDiscovered = 1, // the seed page
            CreatedAt = now,
        };
    }

    public void MarkRunning(DateTimeOffset now)
    {
        if (Status == JobStatus.Pending)
        {
            Status = JobStatus.Running;
            StartedAt = now;
        }
    }

    public Result MarkCompleted(DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return Result.Fail($"Job is already {Status}.");
        }

        Status = JobStatus.Completed;
        CompletedAt = now;
        return Result.Ok();
    }

    public const int MaxReasonLength = CrawlPage.MaxErrorLength;

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        Status = JobStatus.Failed;
        FailureReason = CrawlPage.Truncate(reason);
        CompletedAt = now;
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return Result.Fail($"A {Status} job cannot be canceled.");
        }

        Status = JobStatus.Canceled;
        CompletedAt = now;
        return Result.Ok();
    }

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;

}
