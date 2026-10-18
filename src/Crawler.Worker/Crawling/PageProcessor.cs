using Crawler.Domain.Crawling;
using Crawler.Domain.Model;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Crawling;

public enum ProcessOutcome
{
    Done,      // acknowledge the message
    Transient, // our own infrastructure hiccupped: retry the message later
    Poison,    // the message can never succeed: dead-letter it
}

public sealed record ProcessResult(ProcessOutcome Outcome, string? Reason = null);

/// <summary>
/// Handles exactly one message: "fetch this page for this job". This is the unit the fixture test drives.
///
/// Idempotency: a page row exists (status Queued) before its message does, and the unique key (JobId, Url)
/// means a second delivery finds the row already Fetched and does nothing except re-schedule any of its
/// children still Queued (self-healing if a publish failed after the commit).
///
/// Completion: after each page, "does this job have any page still Queued?" If not, the job is complete.
/// </summary>
public sealed class PageProcessor(
    AppDbContext db,
    PageFetcher fetcher,
    IPagePublisher publisher,
    IOptions<CrawlOptions> options,
    TimeProvider clock,
    ILogger<PageProcessor> logger)
{
    public async Task<ProcessResult> ProcessAsync(CrawlPageMessage message, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == message.JobId, ct);
        if (job is null)
        {
            return new ProcessResult(ProcessOutcome.Poison, $"Job {message.JobId} does not exist.");
        }

        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == message.PageId && p.JobId == job.Id, ct);
        if (page is null)
        {
            return new ProcessResult(ProcessOutcome.Poison, $"Page {message.PageId} does not exist for job {job.Id}.");
        }

        var now = clock.GetUtcNow();

        if (page.Status != PageStatus.Queued)
        {
            // Duplicate delivery. Nothing to redo; just make sure its queued children were scheduled.
            logger.LogInformation("Page {PageId} already {Status}; duplicate delivery ignored", page.Id, page.Status);
            await RescheduleQueuedChildrenAsync(job, page, ct);
            return new ProcessResult(ProcessOutcome.Done);
        }

        if (job.Status is JobStatus.Canceled or JobStatus.Failed)
        {
            page.MarkSkipped($"Job was {job.Status} before this page was fetched.", null, now);
            await db.SaveChangesAsync(ct);
            return new ProcessResult(ProcessOutcome.Done);
        }

        job.MarkRunning(now);

        var fetch = await fetcher.FetchAsync(new Uri(page.Url), ct);
        var scope = new SiteScope(job.StartHost, options.Value.IncludeSubdomains);
        var children = new List<CrawlPage>();

        try
        {
            switch (fetch.Outcome)
            {
                case FetchOutcome.Html when !scope.Contains(fetch.FinalUrl):
                    page.MarkSkipped($"Redirected off the site to {fetch.FinalUrl.Host}.", fetch.StatusCode, clock.GetUtcNow());
                    break;

                case FetchOutcome.Html:
                    var links = LinkExtractor.Extract(fetch.Html!, fetch.FinalUrl);
                    var counts = DomainLinkRatio.Count(links, scope);
                    page.MarkFetched(fetch.FinalUrl, fetch.StatusCode ?? 200, counts.Total, counts.Internal, counts.Ratio, clock.GetUtcNow());

                    foreach (var link in links)
                    {
                        db.Links.Add(PageLink.Create(job.Id, page.Id, link, scope.Contains(link)));
                    }

                    children = await DiscoverChildrenAsync(job, page, links.Where(scope.Contains).ToList(), ct);
                    break;

                case FetchOutcome.NotHtml:
                    page.MarkSkipped(fetch.Error!, fetch.StatusCode, clock.GetUtcNow());
                    break;

                default:
                    page.MarkFailed(fetch.Error!, fetch.StatusCode, clock.GetUtcNow());
                    break;
            }

            if (page.ParentPageId is null && page.Status != PageStatus.Fetched)
            {
                job.MarkFailed($"The starting page could not be crawled: {page.Error}", clock.GetUtcNow());
            }

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another worker discovered the same child page, or raised the cap counter, in the same instant.
            // Nothing is lost: the message is retried and the second pass finds the rows already there.
            logger.LogWarning(ex, "Concurrent write on job {JobId}; message will be retried", job.Id);
            return new ProcessResult(ProcessOutcome.Transient, "Concurrent write; retry.");
        }

        // Schedule children only after their rows are committed, so a message can never refer to a page that is not there yet.
        foreach (var child in children)
        {
            await publisher.PublishAsync(new CrawlPageMessage(job.Id, child.Id, child.Url, child.Depth), ct);
        }

        logger.LogInformation("Page {PageId} {Status} depth {Depth} links {Links} internal {Internal} ratio {Ratio} children {Children}",
            page.Id, page.Status, page.Depth, page.OutgoingLinks, page.InternalLinks, page.DomainLinkRatio, children.Count);

        await CompleteIfFinishedAsync(job, ct);
        return new ProcessResult(ProcessOutcome.Done);
    }

    private async Task<List<CrawlPage>> DiscoverChildrenAsync(CrawlJob job, CrawlPage parent, List<Uri> internalLinks, CancellationToken ct)
    {
        var children = new List<CrawlPage>();
        if (parent.Depth >= job.MaxDepth || internalLinks.Count == 0)
        {
            return children;
        }

        var candidates = internalLinks.Select(l => l.ToString()).Distinct().ToList();
        var known = await db.Pages.Where(p => p.JobId == job.Id && candidates.Contains(p.Url)).Select(p => p.Url).ToHashSetAsync(ct);

        foreach (var url in candidates)
        {
            if (known.Contains(url))
            {
                continue; // already discovered via an earlier page: first parent wins
            }

            if (!job.TryReservePage())
            {
                logger.LogInformation("Job {JobId} reached its page cap of {MaxPages}", job.Id, job.MaxPages);
                break;
            }

            var child = CrawlPage.Discover(job.Id, new Uri(url), parent.Depth + 1, parent.Id, clock.GetUtcNow());
            db.Pages.Add(child);
            children.Add(child);
        }

        return children;
    }

    private async Task RescheduleQueuedChildrenAsync(CrawlJob job, CrawlPage page, CancellationToken ct)
    {
        var queued = await db.Pages.AsNoTracking().Where(p => p.JobId == job.Id && p.ParentPageId == page.Id && p.Status == PageStatus.Queued).ToListAsync(ct);
        foreach (var child in queued)
        {
            await publisher.PublishAsync(new CrawlPageMessage(job.Id, child.Id, child.Url, child.Depth), ct);
        }

        await CompleteIfFinishedAsync(job, ct);
    }

    private async Task CompleteIfFinishedAsync(CrawlJob job, CancellationToken ct)
    {
        if (job.IsTerminal)
        {
            return;
        }

        var stillQueued = await db.Pages.AnyAsync(p => p.JobId == job.Id && p.Status == PageStatus.Queued, ct);
        if (stillQueued)
        {
            return;
        }

        if (job.MarkCompleted(clock.GetUtcNow()).IsSuccess)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Job {JobId} completed with {Pages} pages", job.Id, job.PagesDiscovered);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker completed it in the same instant. Same outcome; nothing to do.
            }
        }
    }
}
