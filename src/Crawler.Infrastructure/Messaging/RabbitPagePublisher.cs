using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

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
        // mandatory + a return handler: a message the broker cannot route (a missing binding) comes BACK instead of
        // vanishing, and the caller learns about it. With confirms on, the return arrives before the confirm.
        BasicReturnEventArgs? returned = null;
        channel.BasicReturnAsync += (_, ea) =>
        {
            returned = ea;
            return Task.CompletedTask;
        };
        try
        {
            await channel.BasicPublishAsync(exchange, RabbitTopology.RoutingKey, mandatory: true, basicProperties: props, body: body, cancellationToken: ct);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            // Newer client versions raise the return as an exception instead of (or as well as) the event.
            throw new UnroutableMessageException(exchange, RabbitTopology.RoutingKey, 312, ex.Message);
        }

        if (returned is { } r)
        {
            throw new UnroutableMessageException(exchange, RabbitTopology.RoutingKey, r.ReplyCode, r.ReplyText);
        }
    }
}
