using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crawler.Api.Tests;

/// <summary>Records every message the API would have put on the queue, so tests can assert on it without a broker.</summary>
public sealed class RecordingPublisher : IPagePublisher
{
    public List<CrawlPageMessage> Published { get; } = [];

    /// <summary>When set, the next publish throws it (a broker outage), then the flag clears.</summary>
    public Exception? FailNextWith { get; set; }

    public Task PublishAsync(CrawlPageMessage message, CancellationToken ct)
    {
        if (FailNextWith is { } outage)
        {
            FailNextWith = null;
            throw outage;
        }

        Published.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class StubHealthCheck(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult verdict) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default) => Task.FromResult(verdict);
}

public sealed class NoopAnnouncer : IProgressAnnouncer
{
    public Task AnnounceAsync(Guid jobId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Boots the real API in-process: SQLite in-memory instead of Postgres, a recording publisher instead of RabbitMQ.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public RecordingPublisher Publisher { get; } = new();

    /// <summary>What the stubbed dependency checks report. Tests never depend on a developer's local broker.</summary>
    public bool DependenciesHealthy { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.UseEnvironment("Testing");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            services.RemoveAll<IPagePublisher>();
            services.AddSingleton<IPagePublisher>(Publisher);
            services.RemoveAll<IProgressAnnouncer>();
            services.AddSingleton<IProgressAnnouncer, NoopAnnouncer>();
            services.Configure<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>(o =>
            {
                o.Registrations.Clear(); // the real checks need Postgres and RabbitMQ; here they are stand-ins with a known answer
                var verdict = DependenciesHealthy ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy() : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("stub: broker down");
                o.Registrations.Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration("database", new StubHealthCheck(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()), null, null));
                o.Registrations.Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration("rabbitmq", new StubHealthCheck(verdict), null, null));
            });
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>(); // no broker in tests: no listener
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
