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
    Done,                      // acknowledge the message
    Transient,                 // a one-off failure (concurrent write, unexpected exception): retry, spending an attempt
    InfrastructureUnavailable, // database or broker unreachable: retry later WITHOUT spending an attempt
    Poison,                    // the message can never succeed: dead-letter it
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
    IProgressAnnouncer announcer,
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
        var seed = new Uri(job.InputUrl);
        var children = new List<CrawlPage>();

        // One transaction for the page, its links, the cap reservation and the new child rows: all or nothing.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
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

                    children = await DiscoverChildrenAsync(job, page, links.Where(scope.Contains).Select(l => scope.Canonicalize(l, seed)).ToList(), ct);
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
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (Messaging.FailureKind.IsInfrastructureUnavailable(ex))
        {
            logger.LogWarning(ex, "Database unavailable while saving job {JobId}; message will wait", job.Id);
            return new ProcessResult(ProcessOutcome.InfrastructureUnavailable, "Database unavailable.");
        }
        catch (DbUpdateException ex)
        {
            // Another worker discovered the same child page in the same instant (unique index), or the job's
            // status changed under us. Nothing is lost: the message is retried and the second pass finds the rows.
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
        await announcer.AnnounceAsync(job.Id, ct); // "go look": the page settled and maybe the job finished
        return new ProcessResult(ProcessOutcome.Done);
    }

    /// <summary>
    /// Called by the consumer when a message has exhausted its retries. The message goes to the dead-letter
    /// queue, but the PAGE must not stay Queued forever or the job would never complete: record the give-up on
    /// the page, fail the job if it was the starting page, and run the completion check.
    /// </summary>
    public async Task GiveUpAsync(CrawlPageMessage message, string reason, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == message.JobId, ct);
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == message.PageId && p.JobId == message.JobId, ct);
        if (job is null || page is null || page.Status != PageStatus.Queued)
        {
            return;
        }

        var now = clock.GetUtcNow();
        page.MarkFailed($"Gave up after {message.Attempt} attempts: {reason}", null, now);
        if (page.ParentPageId is null)
        {
            job.MarkFailed($"The starting page could not be processed: {reason}", now);
        }

        await db.SaveChangesAsync(ct);
        logger.LogError("Page {PageId} marked Failed after {Attempt} attempts: {Reason}", page.Id, message.Attempt, reason);
        await CompleteIfFinishedAsync(job, ct);
        await announcer.AnnounceAsync(job.Id, ct);
    }

    private async Task<List<CrawlPage>> DiscoverChildrenAsync(CrawlJob job, CrawlPage parent, List<Uri> internalLinks, CancellationToken ct)
    {
        var children = new List<CrawlPage>();
        if (parent.Depth >= job.MaxDepth || internalLinks.Count == 0)
        {
            return children;
        }

        var candidates = internalLinks.Select(l => l.ToString()).Distinct().ToList();
        var known = (await db.Pages.Where(p => p.JobId == job.Id && candidates.Contains(p.Url)).Select(p => p.Url).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var fresh = candidates.Where(u => !known.Contains(u)).ToList(); // already discovered via an earlier page: first parent wins

        var granted = await ReservePagesAsync(job, fresh.Count, ct);
        if (granted < fresh.Count)
        {
            logger.LogInformation("Job {JobId} reached its page cap of {MaxPages}; {Dropped} link(s) not followed", job.Id, job.MaxPages, fresh.Count - granted);
        }

        foreach (var url in fresh.Take(granted))
        {
            var child = CrawlPage.Discover(job.Id, new Uri(url), parent.Depth + 1, parent.Id, clock.GetUtcNow());
            db.Pages.Add(child);
            children.Add(child);
        }

        return children;
    }

    /// <summary>
    /// Raise the job's page counter by up to <paramref name="wanted"/> with one conditional UPDATE, so several
    /// workers can reserve at once and the sum can never exceed MaxPages. Returns how many were granted.
    /// </summary>
    private async Task<int> ReservePagesAsync(CrawlJob job, int wanted, CancellationToken ct)
    {
        if (wanted == 0)
        {
            return 0;
        }

        var updated = await db.Jobs
            .Where(j => j.Id == job.Id && j.PagesDiscovered + wanted <= j.MaxPages)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PagesDiscovered, j => j.PagesDiscovered + wanted), ct);
        if (updated == 1)
        {
            return wanted;
        }

        // Not enough room for all of them: take whatever is left, if anything.
        var current = await db.Jobs.Where(j => j.Id == job.Id).Select(j => j.PagesDiscovered).SingleAsync(ct);
        var remaining = Math.Max(job.MaxPages - current, 0);
        if (remaining == 0)
        {
            return 0;
        }

        updated = await db.Jobs
            .Where(j => j.Id == job.Id && j.PagesDiscovered + remaining <= j.MaxPages)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PagesDiscovered, j => j.PagesDiscovered + remaining), ct);
        return updated == 1 ? remaining : 0;
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
