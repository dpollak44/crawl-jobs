using Crawler.Domain.Model;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Tests;

/// <summary>Records what the worker would have put on the queue, and lets a test drain it like a broker would.</summary>
public sealed class InMemoryQueue : IPagePublisher
{
    public Queue<CrawlPageMessage> Pending { get; } = new();
    public List<CrawlPageMessage> AllPublished { get; } = [];

    public Task PublishAsync(CrawlPageMessage message, CancellationToken ct)
    {
        Pending.Enqueue(message);
        AllPublished.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Everything the worker needs, with the two outside dependencies replaced: a SQLite in-memory database
/// instead of Postgres, and <see cref="FakeSite"/> instead of the internet. A fresh DbContext per message,
/// exactly as the real consumer scopes it.
/// </summary>
public sealed class WorkerHarness : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public FakeSite Site { get; } = new();
    public InMemoryQueue Queue { get; } = new();
    public CrawlOptions Options { get; } = new() { MaxBodyBytes = 1024 * 1024 };
    public DateTimeOffset Now { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public WorkerHarness()
    {
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public AppDbContext Db() => new(_dbOptions);

    /// <summary>What the API does: job row + seed page row, then one message.</summary>
    public async Task<(CrawlJob Job, CrawlPage Seed)> StartJobAsync(string url, int maxDepth = 2, int maxPages = 200)
    {
        await using var db = Db();
        var seedUrl = new Uri(url);
        var job = CrawlJob.Create(seedUrl, maxDepth, maxPages, Now);
        var seed = CrawlPage.Discover(job.Id, seedUrl, 0, null, Now);
        db.Jobs.Add(job);
        db.Pages.Add(seed);
        await db.SaveChangesAsync();
        await Queue.PublishAsync(new CrawlPageMessage(job.Id, seed.Id, seed.Url, 0), CancellationToken.None);
        return (job, seed);
    }

    public async Task<ProcessResult> ProcessAsync(CrawlPageMessage message)
    {
        await using var db = Db();
        var http = new HttpClient(Site, disposeHandler: false);
        var fetcher = new PageFetcher(http, Microsoft.Extensions.Options.Options.Create(Options));
        var processor = new PageProcessor(db, fetcher, Queue, Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, NullLogger<PageProcessor>.Instance);
        return await processor.ProcessAsync(message, CancellationToken.None);
    }

    /// <summary>Act as a single worker: process messages until the queue is empty.</summary>
    public async Task<int> DrainAsync()
    {
        var processed = 0;
        while (Queue.Pending.TryDequeue(out var message))
        {
            (await ProcessAsync(message)).Outcome.ShouldBe(ProcessOutcome.Done);
            processed++;
        }

        return processed;
    }

    public void Dispose()
    {
        _connection.Dispose();
        Site.Dispose();
    }
}
