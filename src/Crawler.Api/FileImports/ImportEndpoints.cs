using Crawler.Api.Data;
using Crawler.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.FileImports;

public sealed record RejectionResponse(int LineNumber, string RawLine, string Reason);

/// <summary>Tie-out contract: RowsRead == Accepted + Rejected + Duplicates. A test holds the API to it.</summary>
public sealed record ImportSummaryResponse(Guid ImportId, string FileName, int RowsRead, int Accepted, int Rejected, int Duplicates, IReadOnlyList<RejectionResponse> Rejections);

public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/imports").WithTags("Imports").DisableAntiforgery();

        group.MapPost("/claims", ImportClaimsAsync);
        group.MapGet("/{id:guid}", GetAsync);

        return app;
    }

    private static async Task<Results<Ok<ImportSummaryResponse>, BadRequest<string>>> ImportClaimsAsync(
        IFormFile file,
        AppDbContext db,
        TimeProvider clock,
        IConfiguration config,
        ILogger<ImportRun> logger,
        CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return TypedResults.BadRequest("The uploaded file is empty.");
        }

        var maxBytes = config.GetValue("Import:MaxFileBytes", 10 * 1024 * 1024L);
        if (file.Length > maxBytes)
        {
            return TypedResults.BadRequest($"The file is {file.Length / 1024 / 1024} MB; the limit is {maxBytes / 1024 / 1024} MB. Split it and import the parts.");
        }

        var lines = new List<string>();
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lines.Add(line);
            }
        }

        var parsed = ClaimRowParser.Parse(lines);
        var now = clock.GetUtcNow();

        // Idempotent: a reference we already hold is counted, not re-inserted and not rejected.
        var incomingRefs = parsed.Accepted.Select(r => r.ExternalRef).ToList();
        var known = await db.Claims.Where(c => incomingRefs.Contains(c.ExternalRef)).Select(c => c.ExternalRef).ToHashSetAsync(ct);
        var seenInFile = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;

        foreach (var row in parsed.Accepted)
        {
            if (known.Contains(row.ExternalRef) || !seenInFile.Add(row.ExternalRef))
            {
                duplicates++;
                continue;
            }

            db.Claims.Add(Claim.Create(row.ExternalRef, row.PatientName, row.Amount, now));
        }

        var run = new ImportRun
        {
            Id = Guid.NewGuid(),
            FileName = file.FileName,
            ReceivedAt = now,
            RowsRead = parsed.RowsRead,
            Accepted = parsed.Accepted.Count - duplicates,
            Rejected = parsed.Rejected.Count,
            Duplicates = duplicates,
            Rejections = parsed.Rejected.Select(r => new ImportRejection { LineNumber = r.LineNumber, RawLine = r.RawLine, Reason = r.Reason }).ToList(),
        };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Import {ImportId} of {FileName}: read {RowsRead}, accepted {Accepted}, rejected {Rejected}, duplicates {Duplicates}",
            run.Id, run.FileName, run.RowsRead, run.Accepted, run.Rejected, run.Duplicates);

        return TypedResults.Ok(ToResponse(run));
    }

    private static async Task<Results<Ok<ImportSummaryResponse>, NotFound>> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var run = await db.ImportRuns.AsNoTracking().Include(r => r.Rejections).FirstOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(run));
    }

    private static ImportSummaryResponse ToResponse(ImportRun run) => new(
        run.Id, run.FileName, run.RowsRead, run.Accepted, run.Rejected, run.Duplicates,
        run.Rejections.OrderBy(r => r.LineNumber).Select(r => new RejectionResponse(r.LineNumber, r.RawLine, r.Reason)).ToList());
}
