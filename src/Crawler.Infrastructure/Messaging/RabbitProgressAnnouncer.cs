using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

public sealed class RabbitProgressAnnouncer(RabbitConnection connection, ILogger<RabbitProgressAnnouncer> logger) : IProgressAnnouncer
{
    public async Task AnnounceAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            var conn = await connection.GetAsync(ct);
            await using var channel = await conn.CreateChannelAsync(cancellationToken: ct);
            var props = new BasicProperties { Persistent = false, ContentType = "text/plain" }; // a notification, not work
            await channel.BasicPublishAsync(RabbitTopology.ProgressExchange, string.Empty, mandatory: false, basicProperties: props,
                body: Encoding.UTF8.GetBytes(jobId.ToString()), cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not announce progress for job {JobId}; streams fall back to their safety re-read", jobId);
        }
    }
}
