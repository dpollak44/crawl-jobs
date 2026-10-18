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

    public Task PublishAsync(CrawlPageMessage message, CancellationToken ct)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Boots the real API in-process: SQLite in-memory instead of Postgres, a recording publisher instead of RabbitMQ.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public RecordingPublisher Publisher { get; } = new();

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
