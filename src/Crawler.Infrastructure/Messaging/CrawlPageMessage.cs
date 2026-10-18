namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// The one message in the system: "fetch this page for this job". Carries ids, not state; the database is
/// the record. Attempt counts message-level retries (our own infrastructure failing), not HTTP retries.
/// </summary>
public sealed record CrawlPageMessage(Guid JobId, Guid PageId, string Url, int Depth, int Attempt = 1);
