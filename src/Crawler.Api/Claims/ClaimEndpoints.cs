using Crawler.Api.Data;
using Crawler.Api.Outbound;
using Crawler.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Claims;

public static class ClaimEndpoints
{
    public static IEndpointRouteBuilder MapClaimEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/claims").WithTags("Claims");

        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/submit", SubmitAsync);
        group.MapGet("/{id:guid}/history", HistoryAsync);
        group.MapPost("/{id:guid}/send", SendAsync);

        return app;
    }

    private static async Task<Results<Created<ClaimResponse>, ValidationProblem, Conflict<string>>> CreateAsync(
        CreateClaimRequest request,
        IValidator<CreateClaimRequest> validator,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return TypedResults.ValidationProblem(validation.ToDictionary());
        }

        if (await db.Claims.AnyAsync(c => c.ExternalRef == request.ExternalRef, ct))
        {
            return TypedResults.Conflict($"A claim with reference '{request.ExternalRef}' already exists.");
        }

        var claim = Claim.Create(request.ExternalRef, request.PatientName, request.Amount, clock.GetUtcNow());
        db.Claims.Add(claim);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/claims/{claim.Id}", ClaimResponse.From(claim));
    }

    private static async Task<Results<Ok<ClaimResponse>, NotFound>> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return claim is null ? TypedResults.NotFound() : TypedResults.Ok(ClaimResponse.From(claim));
    }

    private static async Task<Results<Ok<ClaimResponse>, NotFound, ProblemHttpResult>> SubmitAsync(
        Guid id,
        AppDbContext db,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var claim = await db.Claims.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null)
        {
            return TypedResults.NotFound();
        }

        var before = claim.Status.ToString();
        var result = claim.Submit();
        if (!result.IsSuccess)
        {
            return TypedResults.Problem(result.Error, statusCode: StatusCodes.Status409Conflict, title: "Claim cannot be submitted");
        }

        // Audit row and state change land in the same SaveChanges: never one without the other.
        db.AuditEntries.Add(AuditEntry.For("Claim", claim.Id, "Submit", before, claim.Status.ToString(), Actor(http), clock.GetUtcNow()));
        await db.SaveChangesAsync(ct); // a stale Version here throws DbUpdateConcurrencyException -> 409 via GlobalExceptionHandler
        return TypedResults.Ok(ClaimResponse.From(claim));
    }

    private static async Task<Results<Ok<List<ClaimHistoryEntry>>, NotFound>> HistoryAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Claims.AnyAsync(c => c.Id == id, ct))
        {
            return TypedResults.NotFound();
        }

        var rows = await db.AuditEntries.AsNoTracking()
            .Where(a => a.Entity == "Claim" && a.EntityId == id)
            .OrderBy(a => a.At).ThenBy(a => a.Id)
            .Select(a => new ClaimHistoryEntry(a.Action, a.From, a.To, a.Actor, a.At))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    /// <summary>Replace with http.User.Identity.Name once authentication exists.</summary>
    private static string Actor(HttpContext http) =>
        http.User.Identity?.Name ?? http.Request.Headers["X-Actor"].FirstOrDefault() ?? "anonymous";

    /// <summary>Example integration: send the claim to the payer. Records an OutboundCall first, then dispatches it.</summary>
    private static async Task<Results<Ok<OutboundCallResponse>, NotFound>> SendAsync(
        Guid id,
        AppDbContext db,
        OutboundDispatcher dispatcher,
        TimeProvider clock,
        CancellationToken ct)
    {
        var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null)
        {
            return TypedResults.NotFound();
        }

        var call = OutboundCall.Create("payer", System.Text.Json.JsonSerializer.Serialize(ClaimResponse.From(claim)), clock.GetUtcNow());
        db.OutboundCalls.Add(call);
        await db.SaveChangesAsync(ct);

        await dispatcher.DispatchAsync(call, ct);
        return TypedResults.Ok(OutboundCallResponse.From(call));
    }
}
