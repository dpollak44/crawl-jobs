using System.Text.Json;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

public sealed class RabbitPagePublisher(RabbitConnection connection) : IPagePublisher
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task PublishAsync(CrawlPageMessage message, CancellationToken ct) => PublishAsync(RabbitTopology.WorkExchange, message, ct);

    public async Task PublishAsync(string exchange, CrawlPageMessage message, CancellationToken ct)
    {
        var conn = await connection.GetAsync(ct);
        // Publisher confirms: the await completes only when the broker has taken responsibility for the message.
        await using var channel = await conn.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var props = new BasicProperties
        {
            Persistent = true, // survives a broker restart
            ContentType = "application/json",
            MessageId = $"{message.PageId}:{message.Attempt}",
            CorrelationId = message.JobId.ToString(),
        };
        await channel.BasicPublishAsync(exchange, RabbitTopology.RoutingKey, mandatory: false, basicProperties: props, body: body, cancellationToken: ct);
    }
}
