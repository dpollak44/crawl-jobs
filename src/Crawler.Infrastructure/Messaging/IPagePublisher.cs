namespace Crawler.Infrastructure.Messaging;

/// <summary>The only thing the API and the worker need from the queue: "schedule this page". Faked in tests.</summary>
public interface IPagePublisher
{
    Task PublishAsync(CrawlPageMessage message, CancellationToken ct);
}
