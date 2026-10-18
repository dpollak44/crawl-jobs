namespace Crawler.Infrastructure.Messaging;

public sealed class RabbitOptions
{
    public const string Section = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "guest";

    /// <summary>How long a message waits in the retry queue before coming back.</summary>
    public int RetryDelayMs { get; set; } = 5000;

    /// <summary>Total deliveries of one message before it is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How many messages one worker takes at a time.</summary>
    public ushort Prefetch { get; set; } = 8;
}
