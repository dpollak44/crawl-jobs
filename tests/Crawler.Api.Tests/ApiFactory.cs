using Crawler.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crawler.Api.Tests;

/// <summary>
/// Boots the real API in-process against a fresh SQLite in-memory database per factory, with the external
/// system replaced by <see cref="FakePayerHandler"/> and retry delays shortened to milliseconds.
/// The open connection keeps the in-memory database alive for the factory's lifetime.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public FakePayerHandler Payer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.UseEnvironment("Testing");
        builder.UseSetting("Resilience:Retry:Delay", "00:00:00.010");
        builder.UseSetting("Resilience:Retry:UseJitter", "false");
        builder.UseSetting("Logging:LogLevel:Default", "Warning"); // keep test output readable
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));

            services.AddSingleton(Payer);
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<FakePayerHandler>()));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            Payer.Dispose();
        }
    }
}
