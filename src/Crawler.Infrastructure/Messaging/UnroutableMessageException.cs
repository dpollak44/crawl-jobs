namespace Crawler.Infrastructure.Messaging;

/// <summary>The broker accepted the publish but had nowhere to deliver it (no queue bound). Silent loss made loud.</summary>
public sealed class UnroutableMessageException(string exchange, string routingKey, ushort replyCode, string replyText)
    : Exception($"Message to exchange '{exchange}' with key '{routingKey}' was returned by the broker: {replyCode} {replyText}. Nothing is bound to receive it.")
{
    public string Exchange { get; } = exchange;
    public string RoutingKey { get; } = routingKey;
}
