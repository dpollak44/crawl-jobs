using Crawler.Domain.Model;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Crawling;

/// <summary>
/// Closes the one gap in "the queue is the to-do list": a worker that dies after committing a page but
/// before publishing its children leaves those children Queued with no message in flight. Every
/// SweepIntervalSeconds, re-publish any page that has sat Queued for longer than StuckAfterSeconds on a job
/// that is still running. Safe to run on every worker: a page that was merely slow gets a duplicate message,
/// and duplicates are ignored by design.
/// </summary>
public sealed class QueuedPageSweeper(IServiceScopeFactory scopes, IOptions<CrawlOptions> options, TimeProvider clock, ILogger<QueuedPageSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.SweepIntervalSeconds, 5));
        using var timer = new PeriodicTimer(interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var publisher = scope.ServiceProvider.GetRequiredService<IPagePublisher>();
                var republished = await SweepOnceAsync(db, publisher, clock.GetUtcNow(), TimeSpan.FromSeconds(options.Value.StuckAfterSeconds), stoppingToken);
                if (republished > 0)
                {
                    logger.LogWarning("Sweeper re-published {Count} page(s) left Queued for more than {Seconds}s", republished, options.Value.StuckAfterSeconds);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweep failed; will try again next interval");
            }
        }
    }

    /// <summary>One pass, separated from the timer so it can be tested directly. Returns how many messages were re-published.</summary>
    public static async Task<int> SweepOnceAsync(AppDbContext db, IPagePublisher publisher, DateTimeOffset now, TimeSpan stuckAfter, CancellationToken ct)
    {
        var cutoff = now - stuckAfter;
        var activeStatuses = new[] { JobStatus.Pending, JobStatus.Running };

        var stuck = await db.Pages.AsNoTracking()
            .Where(p => p.Status == PageStatus.Queued && p.LastQueuedAt < cutoff)
            .Join(db.Jobs.Where(j => activeStatuses.Contains(j.Status)), p => p.JobId, j => j.Id, (p, j) => p)
            .OrderBy(p => p.LastQueuedAt)
            .Take(500)
            .ToListAsync(ct);

        if (stuck.Count == 0)
        {
            return 0;
        }

        // Claim each page with a conditional stamp and publish only the ones THIS worker claimed: two workers sweeping
        // at the same moment read the same list, but only one of them wins each row.
        var sent = 0;
        foreach (var page in stuck)
        {
            var claimed = await db.Pages.Where(p => p.Id == page.Id && p.LastQueuedAt < cutoff)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastQueuedAt, now), ct);
            if (claimed == 1)
            {
                await publisher.PublishAsync(new CrawlPageMessage(page.JobId, page.Id, page.Url, page.Depth), ct);
                sent++;
            }
        }

        return sent;
    }
}
