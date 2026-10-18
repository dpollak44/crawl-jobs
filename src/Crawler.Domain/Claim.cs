namespace Crawler.Domain;

public enum ClaimStatus
{
    Draft,
    Submitted,
    Paid,
    Denied,
}

/// <summary>
/// Example aggregate: a healthcare claim with a small state machine. Replace with the assignment's domain.
/// Rules live here so they are unit-testable without the API or a database.
/// </summary>
public sealed class Claim
{
    public Guid Id { get; private set; }

    /// <summary>The caller's identifier (e.g. a remit or claim number from a file). Unique; makes imports idempotent.</summary>
    public string ExternalRef { get; private set; }
    public string PatientName { get; private set; }
    public decimal Amount { get; private set; }
    public ClaimStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Optimistic concurrency token. Bumped on every change; a stale writer gets a conflict, not a silent overwrite.</summary>
    public int Version { get; private set; }

    // EF Core needs a parameterless constructor; keep it private so callers go through Create().
    private Claim()
    {
        ExternalRef = string.Empty;
        PatientName = string.Empty;
    }

    public static Claim Create(string externalRef, string patientName, decimal amount, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(externalRef))
        {
            throw new ArgumentException("External reference is required.", nameof(externalRef));
        }

        if (string.IsNullOrWhiteSpace(patientName))
        {
            throw new ArgumentException("Patient name is required.", nameof(patientName));
        }

        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        }

        return new Claim
        {
            Id = Guid.NewGuid(),
            ExternalRef = externalRef.Trim(),
            PatientName = patientName.Trim(),
            Amount = amount,
            Status = ClaimStatus.Draft,
            CreatedAt = now,
            Version = 1,
        };
    }

    public Result Submit()
    {
        if (Status != ClaimStatus.Draft)
        {
            return Result.Fail($"Only a Draft claim can be submitted (current: {Status}).");
        }

        Status = ClaimStatus.Submitted;
        Version++;
        return Result.Ok();
    }

    public Result Adjudicate(bool approved)
    {
        if (Status != ClaimStatus.Submitted)
        {
            return Result.Fail($"Only a Submitted claim can be adjudicated (current: {Status}).");
        }

        Status = approved ? ClaimStatus.Paid : ClaimStatus.Denied;
        Version++;
        return Result.Ok();
    }
}
