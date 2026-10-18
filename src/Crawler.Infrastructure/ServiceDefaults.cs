using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Crawler.Infrastructure;

/// <summary>
/// The twenty lines every service needs, written once: JSON logs, the database, the queue, health checks.
/// Both the API and the worker call this so they can never disagree about how to reach the database or the broker.
/// </summary>
public static class ServiceDefaults
{
    public static WebApplicationBuilder AddCrawlerDefaults(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.ClearProviders().AddJsonConsole();
        }

        var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=crawler.db";
        builder.Services.AddDbContext<AppDbContext>(o =>
        {
            if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
            {
                o.UseNpgsql(connectionString);
            }
            else
            {
                o.UseSqlite(connectionString); // local runs and tests
            }
        });

        builder.Services.Configure<RabbitOptions>(builder.Configuration.GetSection(RabbitOptions.Section));
        builder.Services.AddSingleton<RabbitConnection>();
        builder.Services.AddSingleton<RabbitPagePublisher>();
        builder.Services.AddSingleton<IPagePublisher>(sp => sp.GetRequiredService<RabbitPagePublisher>());
        builder.Services.AddSingleton<IProgressAnnouncer, RabbitProgressAnnouncer>();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database")
            .AddCheck<RabbitHealthCheck>("rabbitmq");

        return builder;
    }

    /// <summary>Create the schema on boot. Take-home scale; a real release runs EF migrations as a deploy step.</summary>
    public static async Task EnsureDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
    }
}

public sealed class RabbitHealthCheck(RabbitConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var conn = await connection.GetAsync(cancellationToken);
            return conn.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("connection closed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
