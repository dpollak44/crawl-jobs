using System.Text;
using Crawler.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Api.Jobs;

/// <summary>
/// Listens on the progress fanout and hands each "job X changed" to the relay. The queue is exclusive,
/// auto-delete and server-named: every API instance gets its own copy of every notification, and the queue
/// disappears with the instance, so several API instances scale out with nothing to coordinate.
/// </summary>
public sealed class ProgressListener(RabbitConnection connection, ProgressRelay relay, ILogger<ProgressListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var conn = await connection.GetAsync(stoppingToken);
                await using var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
                var queue = await channel.QueueDeclareAsync(queue: string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(queue.QueueName, RabbitTopology.ProgressExchange, string.Empty, cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    if (Guid.TryParse(Encoding.UTF8.GetString(ea.Body.Span), out var jobId))
                    {
                        var pushed = await relay.NotifyAsync(jobId, stoppingToken);
                        logger.LogDebug("Progress notification for job {JobId}: {Outcome}", jobId, pushed ? "pushed to open streams" : "no listeners");
                    }
                };
                await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer, stoppingToken);
                logger.LogInformation("Relaying progress notifications from {Exchange} via {Queue}", RabbitTopology.ProgressExchange, queue.QueueName);
                delay = TimeSpan.FromSeconds(1);

                await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Progress listener lost the broker; retrying in {Delay}s (streams keep their safety re-read)", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }
}
