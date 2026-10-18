namespace Crawler.Infrastructure.Messaging;

/// <summary>"Job X changed; go look." Carries no data. A failure to announce is logged and swallowed: it must never fail the work.</summary>
public interface IProgressAnnouncer
{
    Task AnnounceAsync(Guid jobId, CancellationToken ct);
}
