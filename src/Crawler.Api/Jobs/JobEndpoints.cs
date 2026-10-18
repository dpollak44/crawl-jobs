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
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return TypedResults.Accepted($"/api/jobs/{job.Id}", new CreateJobResponse(job.Id));
    }

    private static async Task<Results<Ok<JobSummary>, NotFound>> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        var counts = await db.Pages.AsNoTracking().Where(p => p.JobId == id)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return TypedResults.Ok(ToSummary(job, counts.ToDictionary(c => c.Status, c => c.Count)));
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

    private static async Task<Results<Ok<JobSummary>, NotFound, ProblemHttpResult>> CancelAsync(Guid id, AppDbContext db, TimeProvider clock, CancellationToken ct)
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
        return TypedResults.Ok(ToSummary(job, []));
    }

    private static JobSummary ToSummary(CrawlJob job, Dictionary<PageStatus, int> counts)
    {
        int Of(PageStatus s) => counts.GetValueOrDefault(s);
        var progress = new JobProgress(job.PagesDiscovered, Of(PageStatus.Fetched), Of(PageStatus.Failed), Of(PageStatus.Skipped), Of(PageStatus.Queued));
        return new JobSummary(job.Id, job.InputUrl, StatusText.Of(job.Status), job.MaxDepth, job.MaxPages, job.CreatedAt, job.StartedAt, job.CompletedAt, job.FailureReason, progress);
    }
}
