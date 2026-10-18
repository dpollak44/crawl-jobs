namespace Crawler.Domain;

public enum OutboundStatus
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>
/// A record of one call we owe an external system (payer, clearinghouse, CRM).
/// Written before the call is attempted, so a crash or a down partner leaves a visible, retryable row
/// instead of a lost message. Transient failures are retried automatically by the HTTP client;
/// this row is for the failures that outlived those retries and need a human or a scheduled retry.
/// </summary>
public sealed class OutboundCall
{
    public Guid Id { get; private set; }
    public string Target { get; private set; }
    public string Payload { get; private set; }
    public OutboundStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }

    private OutboundCall()
    {
        Target = string.Empty;
        Payload = string.Empty;
    }

    public static OutboundCall Create(string target, string payload, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Target = target,
        Payload = payload,
        Status = OutboundStatus.Pending,
        CreatedAt = now,
    };

    public void MarkSucceeded(DateTimeOffset now)
    {
        Attempts++;
        LastAttemptAt = now;
        LastError = null;
        Status = OutboundStatus.Succeeded;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastAttemptAt = now;
        LastError = error;
        Status = OutboundStatus.Failed;
    }

    public Result PrepareRetry()
    {
        if (Status == OutboundStatus.Succeeded)
        {
            return Result.Fail("This call already succeeded; there is nothing to retry.");
        }

        Status = OutboundStatus.Pending;
        return Result.Ok();
    }
}
