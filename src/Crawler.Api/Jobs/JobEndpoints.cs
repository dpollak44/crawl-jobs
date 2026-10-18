using System.Text.Json;
using Crawler.Domain.Crawling;
using Crawler.Domain.Model;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crawler.Api.Jobs;

/// <summary>
/// The API creates jobs and answers questions. It never crawls. Endpoints are thin: validate, write, publish, map.
/// </summary>
public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").WithTags("Jobs");

        group.MapPost("/", CreateAsync);
        group.MapGet("/", HistoryAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/events", StreamAsync)
            .WithDescription("Live progress as Server-Sent Events: the current summary on connect, then a line whenever it changes, then `event: done`. Same JSON as GET /api/jobs/{id}.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/{id:guid}/tree", TreeAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);

        return app;
    }

    private static async Task<Results<Accepted<CreateJobResponse>, ValidationProblem>> CreateAsync(
        CreateJobRequest request,
        IValidator<CreateJobRequest> validator,
        AppDbContext db,
        IPagePublisher publisher,
        IOptions<CrawlOptions> crawl,
        TimeProvider clock,
        ILogger<CrawlJob> logger,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return TypedResults.ValidationProblem(validation.ToDictionary());
        }

        var seedUrl = new Uri(request.UrlWithScheme, UriKind.Absolute);
        UrlNormalizer.TryNormalize(seedUrl.ToString(), seedUrl, out var normalizedSeed);

        var now = clock.GetUtcNow();
        var job = CrawlJob.Create(normalizedSeed, request.MaxDepth ?? crawl.Value.DefaultMaxDepth, request.MaxPages ?? crawl.Value.DefaultMaxPages, now);
        var seed = CrawlPage.Discover(job.Id, normalizedSeed, depth: 0, parentPageId: null, now);
        db.Jobs.Add(job);
        db.Pages.Add(seed);
        await db.SaveChangesAsync(ct);

        // The job exists before the message does. If the broker is down, the user sees a Failed job with the
        // reason, not a job stuck at Pending forever. The thorough fix is an outbox (see README, next steps).
        try
        {
            // The job is saved; finish scheduling it even if the caller disconnected this instant (CancellationToken.None).
            await publisher.PublishAsync(new CrawlPageMessage(job.Id, seed.Id, seed.Url, 0), CancellationToken.None);
            logger.LogInformation("Job {JobId} created for {Url} (depth {MaxDepth}, cap {MaxPages})", job.Id, job.InputUrl, job.MaxDepth, job.MaxPages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {JobId} could not be scheduled: broker unavailable", job.Id);
            job.MarkFailed("Could not schedule the crawl: the message broker is unavailable. Try again in a minute.", clock.GetUtcNow());
            seed.MarkFailed("Never scheduled: the message broker was unavailable.", null, clock.GetUtcNow()); // nothing left Queued: the screen settles
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return TypedResults.Accepted($"/api/jobs/{job.Id}", new CreateJobResponse(job.Id));
    }

    private static async Task<Results<Ok<JobSummary>, NotFound>> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var summary = await LoadSummaryAsync(db, id, ct);
        return summary is null ? TypedResults.NotFound() : TypedResults.Ok(summary);
    }

    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Live progress over Server-Sent Events. The browser stops polling. The stream writes the summary on connect,
    /// then whenever the relay pushes a change (the worker announced "job X changed" and the relay read the summary
    /// once for every open stream), with a safety re-read every 10 s in case an announcement was lost. Ends with
    /// `event: done` once the job is final and no page is still queued. A closed browser cancels the request.
    /// </summary>
    private static readonly TimeSpan SafetyReread = TimeSpan.FromSeconds(10);

    private static async Task StreamAsync(Guid id, HttpContext http, AppDbContext db, ProgressRelay relay, CancellationToken ct)
    {
        var current = await LoadSummaryAsync(db, id, ct);
        if (current is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        http.Response.Headers["X-Accel-Buffering"] = "no"; // tell a reverse proxy not to buffer this response

        var (updates, subscription) = relay.Subscribe(id);
        using var _ = subscription;
        string? lastSent = null;
        try
        {
            while (true)
            {
                var json = JsonSerializer.Serialize(current, SseJson);
                if (json != lastSent)
                {
                    await http.Response.WriteAsync($"data: {json}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    lastSent = json;
                }

                var isFinal = current.Status is not ("Pending" or "Running") && current.Progress.Queued == 0;
                if (isFinal)
                {
                    await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    return;
                }

                // Wait for the worker to say "go look", or re-read anyway after SafetyReread in case an announcement was lost.
                using var waitClock = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitClock.CancelAfter(SafetyReread);
                try
                {
                    current = await updates.ReadAsync(waitClock.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    current = await LoadSummaryAsync(db, id, ct);
                    if (current is null)
                    {
                        return; // job deleted underneath us: nothing more to say
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the browser went away; that is the normal way a stream ends early
        }
    }

    internal static async Task<JobSummary?> LoadSummaryAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return null;
        }

        var counts = await db.Pages.AsNoTracking().Where(p => p.JobId == id)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return ToSummary(job, counts.ToDictionary(c => c.Status, c => c.Count));
    }

    private static async Task<Ok<Page<JobSummary>>> HistoryAsync(int? page, int? pageSize, AppDbContext db, IConfiguration config, CancellationToken ct)
    {
        var (skip, take, pageNumber) = Paging.Resolve(page, pageSize, config);
        var total = await db.Jobs.CountAsync(ct);
        var jobs = await db.Jobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);

        var ids = jobs.Select(j => j.Id).ToList();
        var counts = await db.Pages.AsNoTracking().Where(p => ids.Contains(p.JobId))
            .GroupBy(p => new { p.JobId, p.Status })
            .Select(g => new { g.Key.JobId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var items = jobs.Select(j => ToSummary(j, counts.Where(c => c.JobId == j.Id).ToDictionary(c => c.Status, c => c.Count))).ToList();
        return TypedResults.Ok(new Page<JobSummary>(items, pageNumber, take, total));
    }

    private static async Task<Results<Ok<TreeNode>, NotFound, ProblemHttpResult>> TreeAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Jobs.AnyAsync(j => j.Id == id, ct))
        {
            return TypedResults.NotFound();
        }

        // One query, then build the tree in memory: pages per job are capped, so this is bounded.
        var pages = await db.Pages.AsNoTracking().Where(p => p.JobId == id).OrderBy(p => p.Depth).ThenBy(p => p.DiscoveredAt).ToListAsync(ct);
        var root = pages.FirstOrDefault(p => p.ParentPageId is null);
        if (root is null)
        {
            return TypedResults.Problem("This job has no starting page recorded.", statusCode: StatusCodes.Status409Conflict);
        }

        var nodes = pages.ToDictionary(p => p.Id, p => new TreeNode(p.Id, p.Url, p.Depth, StatusText.Of(p.Status), p.DomainLinkRatio, p.InternalLinks, p.OutgoingLinks, p.Error, []));
        foreach (var p in pages.Where(p => p.ParentPageId is not null))
        {
            if (nodes.TryGetValue(p.ParentPageId!.Value, out var parent))
            {
                parent.Children.Add(nodes[p.Id]);
            }
        }

        return TypedResults.Ok(nodes[root.Id]);
    }

    private static async Task<Results<Ok<JobSummary>, NotFound, ProblemHttpResult>> CancelAsync(Guid id, AppDbContext db, TimeProvider clock, IProgressAnnouncer announcer, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        var result = job.Cancel(clock.GetUtcNow());
        if (!result.IsSuccess)
        {
            return TypedResults.Problem(result.Error, statusCode: StatusCodes.Status409Conflict, title: "Job cannot be canceled");
        }

        await db.SaveChangesAsync(ct);
        await announcer.AnnounceAsync(job.Id, CancellationToken.None);
        return TypedResults.Ok((await LoadSummaryAsync(db, id, ct))!);
    }

    private static JobSummary ToSummary(CrawlJob job, Dictionary<PageStatus, int> counts)
    {
        int Of(PageStatus s) => counts.GetValueOrDefault(s);
        var progress = new JobProgress(job.PagesDiscovered, Of(PageStatus.Fetched), Of(PageStatus.Failed), Of(PageStatus.Skipped), Of(PageStatus.Queued));
        return new JobSummary(job.Id, job.InputUrl, StatusText.Of(job.Status), job.MaxDepth, job.MaxPages, job.CreatedAt, job.StartedAt, job.CompletedAt, job.FailureReason, progress);
    }
}
