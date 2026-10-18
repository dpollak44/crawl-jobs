using Crawler.Api.Data;
using Crawler.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Outbound;

public sealed record OutboundCallResponse(Guid Id, string Target, string Status, int Attempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? LastAttemptAt)
{
    public static OutboundCallResponse From(OutboundCall c) => new(c.Id, c.Target, c.Status.ToString(), c.Attempts, c.LastError, c.CreatedAt, c.LastAttemptAt);
}

public static class OutboundEndpoints
{
    public static IEndpointRouteBuilder MapOutboundEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/outbound").WithTags("Outbound");

        group.MapGet("/", ListAsync);
        group.MapPost("/{id:guid}/retry", RetryAsync);

        return app;
    }

    /// <summary>The failure queue a person watches: GET /outbound?status=Failed&amp;page=1&amp;pageSize=50.</summary>
    private static async Task<Ok<Page<OutboundCallResponse>>> ListAsync(
        OutboundStatus? status,
        int? page,
        int? pageSize,
        AppDbContext db,
        IConfiguration config,
        CancellationToken ct)
    {
        var query = db.OutboundCalls.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(c => c.Status == status);
        }

        var (skip, take, pageNumber) = Paging.Resolve(page, pageSize, config);
        var total = await query.CountAsync(ct);
        var calls = await query.OrderByDescending(c => c.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
        return TypedResults.Ok(new Page<OutboundCallResponse>(calls.Select(OutboundCallResponse.From).ToList(), pageNumber, take, total));
    }

    private static async Task<Results<Ok<OutboundCallResponse>, NotFound, ProblemHttpResult>> RetryAsync(
        Guid id,
        AppDbContext db,
        OutboundDispatcher dispatcher,
        CancellationToken ct)
    {
        var call = await db.OutboundCalls.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (call is null)
        {
            return TypedResults.NotFound();
        }

        var prep = call.PrepareRetry();
        if (!prep.IsSuccess)
        {
            return TypedResults.Problem(prep.Error, statusCode: StatusCodes.Status409Conflict, title: "Nothing to retry");
        }

        await dispatcher.DispatchAsync(call, ct);
        return TypedResults.Ok(OutboundCallResponse.From(call));
    }
}
