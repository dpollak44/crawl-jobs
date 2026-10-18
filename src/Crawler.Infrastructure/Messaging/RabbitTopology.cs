using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// All queue wiring in one place so it can be read in one sitting.
///
///   crawl.pages        the work queue. Rejected messages go to the dead-letter exchange.
///   crawl.pages.retry  a holding queue with a fixed TTL; when a message expires it is routed back to crawl.pages.
///   crawl.pages.dead   where poison messages and exhausted retries end up for a human to inspect.
///
/// A transient failure republishes the message to the retry queue with Attempt+1. After MaxAttempts, or
/// immediately for a poison message, it goes to the dead queue.
/// </summary>
public static class RabbitTopology
{
    public const string WorkExchange = "crawl";
    public const string RetryExchange = "crawl.retry";
    public const string DeadExchange = "crawl.dead";
    public const string WorkQueue = "crawl.pages";
    public const string RetryQueue = "crawl.pages.retry";
    public const string DeadQueue = "crawl.pages.dead";
    public const string RoutingKey = "page";

    /// <summary>Fanout for "job X changed" notifications: no data, just "go look". Each API instance binds its own auto-delete queue.</summary>
    public const string ProgressExchange = "crawl.progress";

    public static async Task DeclareAsync(IChannel channel, int retryDelayMs, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(WorkExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(ProgressExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync(WorkQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadExchange }, cancellationToken: ct);
        await channel.QueueBindAsync(WorkQueue, WorkExchange, RoutingKey, cancellationToken: ct);

        // Messages sit here for retryDelayMs, then expire back onto the work exchange.
        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = WorkExchange,
                ["x-dead-letter-routing-key"] = RoutingKey,
                ["x-message-ttl"] = retryDelayMs,
            }, cancellationToken: ct);
        await channel.QueueBindAsync(RetryQueue, RetryExchange, RoutingKey, cancellationToken: ct);

        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadQueue, DeadExchange, RoutingKey, cancellationToken: ct);
    }
}
