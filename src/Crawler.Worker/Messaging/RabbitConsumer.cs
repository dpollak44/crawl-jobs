using System.Text.Json;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Crawling;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Takes messages off the work queue one scope at a time and routes each outcome:
///   Done      → ack
///   Transient → republish to the retry queue with Attempt+1 (ack the original); after MaxAttempts → dead-letter
///   Poison    → reject without requeue, which RabbitMQ routes to the dead-letter exchange
/// Anything unexpected thrown by processing is treated as Transient: our bug or our infrastructure, never the page's fault.
/// </summary>
public sealed class RabbitConsumer(
    RabbitConnection connection,
    RabbitPagePublisher publisher,
    IServiceScopeFactory scopes,
    ILogger<RabbitConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var conn = await ConnectWithPatienceAsync(stoppingToken);
        await using var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: connection.Options.Prefetch, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var deliveryTag = ea.DeliveryTag;
            CrawlPageMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<CrawlPageMessage>(ea.Body.Span, RabbitPagePublisher.Json);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Poison message (unreadable JSON); dead-lettering");
                await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, stoppingToken);
                return;
            }

            if (message is null || message.PageId == Guid.Empty || message.JobId == Guid.Empty)
            {
                logger.LogError("Poison message (missing ids); dead-lettering");
                await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, stoppingToken);
                return;
            }

            using var _ = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = message.JobId, ["PageId"] = message.PageId, ["Attempt"] = message.Attempt });
            ProcessResult result;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<PageProcessor>();
                result = await processor.ProcessAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, CancellationToken.None); // shutting down: give it back
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure processing page {PageId}; treating as transient", message.PageId);
                result = new ProcessResult(ProcessOutcome.Transient, ex.Message);
            }

            switch (result.Outcome)
            {
                case ProcessOutcome.Done:
                    await channel.BasicAckAsync(deliveryTag, multiple: false, stoppingToken);
                    break;

                case ProcessOutcome.Transient when message.Attempt < connection.Options.MaxAttempts:
                    logger.LogWarning("Transient failure on attempt {Attempt}: {Reason}; retrying in {Delay} ms", message.Attempt, result.Reason, connection.Options.RetryDelayMs);
                    await publisher.PublishAsync(RabbitTopology.RetryExchange, message with { Attempt = message.Attempt + 1 }, stoppingToken);
                    await channel.BasicAckAsync(deliveryTag, multiple: false, stoppingToken);
                    break;

                case ProcessOutcome.Transient:
                    logger.LogError("Giving up on page {PageId} after {Attempt} attempts: {Reason}; dead-lettering", message.PageId, message.Attempt, result.Reason);
                    await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, stoppingToken);
                    break;

                case ProcessOutcome.Poison:
                    logger.LogError("Poison message for page {PageId}: {Reason}; dead-lettering", message.PageId, result.Reason);
                    await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, stoppingToken);
                    break;
            }
        };

        await channel.BasicConsumeAsync(RabbitTopology.WorkQueue, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Worker consuming {Queue} (prefetch {Prefetch})", RabbitTopology.WorkQueue, connection.Options.Prefetch);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>The broker may still be starting when the worker does. Wait for it rather than crash-loop.</summary>
    private async Task<IConnection> ConnectWithPatienceAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                return await connection.GetAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Broker not reachable yet ({Error}); retrying in {Delay}s", ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }
}
