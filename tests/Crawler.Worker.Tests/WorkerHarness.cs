using Crawler.Domain.Model;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
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

public sealed class RecordingAnnouncer : IProgressAnnouncer
{
    public List<Guid> Announced { get; } = [];

    public Task AnnounceAsync(Guid jobId, CancellationToken ct)
    {
        Announced.Add(jobId);
        return Task.CompletedTask;
    }
}

/// <summary>A clock the test moves by hand, so creation and processing stamps come from one source.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// Everything the worker needs, with the two outside dependencies replaced: a SQLite in-memory database
/// instead of Postgres, and <see cref="FakeSite"/> instead of the internet. A fresh DbContext per message,
/// exactly as the real consumer scopes it. One clock for creation and processing; every HttpClient the harness
/// hands out is owned and disposed here; the fake site is shared and kept alive.
/// </summary>
public sealed class WorkerHarness : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly List<HttpClient> _clients = [];

    public FakeSite Site { get; } = new();
    public InMemoryQueue Queue { get; } = new();
    public RecordingAnnouncer Announcer { get; } = new();
    public CrawlOptions Options { get; } = new() { MaxBodyBytes = 1024 * 1024 };
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    public DateTimeOffset Now => Clock.GetUtcNow();

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

    /// <summary>A processor over the given context, built as the consumer's scope builds it: the same resilience policy the worker runs with (see FetchPipeline), wrapped around the fake site.</summary>
    public PageProcessor Processor(AppDbContext db)
    {
        var http = new HttpClient(new ResilienceHandler(FetchPipeline.Build(Options)) { InnerHandler = Site }, disposeHandler: false);
        _clients.Add(http);
        var fetcher = new PageFetcher(http, Microsoft.Extensions.Options.Options.Create(Options));
        return new PageProcessor(db, fetcher, Queue, Announcer, Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<PageProcessor>.Instance);
    }

    public async Task<ProcessResult> ProcessAsync(CrawlPageMessage message)
    {
        await using var db = Db();
        return await Processor(db).ProcessAsync(message, CancellationToken.None);
    }

    /// <summary>Act as a single worker: process messages until the queue is empty. The ceiling turns a delivery regression into a failure instead of a hang.</summary>
    public async Task<int> DrainAsync(int ceiling = 500)
    {
        var processed = 0;
        while (Queue.Pending.TryDequeue(out var message))
        {
            (await ProcessAsync(message)).Outcome.ShouldBe(ProcessOutcome.Done);
            processed++;
            processed.ShouldBeLessThanOrEqualTo(ceiling, "the queue never drained");
        }

        return processed;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _connection.Dispose();
        Site.Dispose();
    }
}
