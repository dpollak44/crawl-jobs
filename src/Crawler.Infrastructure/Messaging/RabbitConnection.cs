using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>One connection per process, opened lazily, topology declared once. Channels are cheap; connections are not.</summary>
public sealed class RabbitConnection(IOptions<RabbitOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitOptions Options => options.Value;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var o = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.Host,
                Port = o.Port,
                UserName = o.User,
                Password = o.Password,
                AutomaticRecoveryEnabled = true,
                ConsumerDispatchConcurrency = o.Prefetch, // handle up to Prefetch messages at once per worker
            };
            // Build, declare the topology, and only then publish to the field: a failed declaration is retried next time,
            // and a connection that is being replaced is disposed instead of leaked.
            var fresh = await factory.CreateConnectionAsync(ct);
            try
            {
                await using var channel = await fresh.CreateChannelAsync(cancellationToken: ct);
                await RabbitTopology.DeclareAsync(channel, o.RetryDelayMs, ct);
            }
            catch
            {
                await fresh.DisposeAsync();
                throw;
            }

            var previous = _connection;
            _connection = fresh;
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }

            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The broker may still be starting when a service does: wait for it with backoff (1 s doubling, capped at 15 s) rather than crash-loop.</summary>
    public async Task<IConnection> GetWithPatienceAsync(Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                return await GetAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Broker not reachable yet; retrying in {Delay}s", delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
