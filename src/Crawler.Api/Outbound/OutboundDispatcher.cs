using Crawler.Api.Data;
using Crawler.Domain;

namespace Crawler.Api.Outbound;

/// <summary>
/// Makes one outbound call and records the outcome on its row. Transient errors have already been retried
/// by the HTTP pipeline by the time we get here; what remains is a real failure worth a log line at Error
/// level (that is the alert) and a Failed row a person can retry from the API.
/// </summary>
public sealed class OutboundDispatcher(PayerClient payer, AppDbContext db, TimeProvider clock, ILogger<OutboundDispatcher> logger)
{
    public async Task DispatchAsync(OutboundCall call, CancellationToken ct)
    {
        try
        {
            using var response = await payer.PostAsync(call.Payload, ct);
            if (response.IsSuccessStatusCode)
            {
                call.MarkSucceeded(clock.GetUtcNow());
            }
            else
            {
                call.MarkFailed($"{call.Target} answered {(int)response.StatusCode} {response.ReasonPhrase}", clock.GetUtcNow());
            }
        }
        catch (HttpRequestException ex)
        {
            call.MarkFailed($"{call.Target} unreachable: {ex.Message}", clock.GetUtcNow());
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            call.MarkFailed($"{call.Target} timed out: {ex.Message}", clock.GetUtcNow());
        }

        if (call.Status == OutboundStatus.Failed)
        {
            logger.LogError("Outbound call {CallId} to {Target} failed after {Attempts} attempt(s): {Error}", call.Id, call.Target, call.Attempts, call.LastError);
        }
        else
        {
            logger.LogInformation("Outbound call {CallId} to {Target} succeeded on attempt {Attempts}", call.Id, call.Target, call.Attempts);
        }

        await db.SaveChangesAsync(ct);
    }
}
