using Crawler.Infrastructure;
using Crawler.Worker.Crawling;
using Crawler.Worker.Messaging;
using Microsoft.Extensions.Http.Resilience;

// The worker is a web host too, for one reason: a /health endpoint the orchestrator can probe.
var builder = WebApplication.CreateBuilder(args);
builder.AddCrawlerDefaults();
builder.Services.Configure<CrawlOptions>(builder.Configuration.GetSection(CrawlOptions.Section));

var crawl = builder.Configuration.GetSection(CrawlOptions.Section).Get<CrawlOptions>() ?? new CrawlOptions();

// Fetching other people's websites: redirect cap, per-attempt timeout, retries with backoff, circuit breaker.
builder.Services.AddHttpClient<PageFetcher>(c =>
    {
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CrawlJobs/1.0 (+https://github.com/dpollak44/crawl-jobs)");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = crawl.MaxRedirects,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(crawl.FetchTimeoutSeconds);
        o.Retry.MaxRetryAttempts = crawl.FetchRetries;
        o.Retry.Delay = TimeSpan.FromMilliseconds(500);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(crawl.FetchTimeoutSeconds * (crawl.FetchRetries + 1) + 5);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(crawl.FetchTimeoutSeconds * 2, 30));
    });

builder.Services.AddScoped<PageProcessor>();
builder.Services.AddHostedService<RabbitConsumer>();

var app = builder.Build();
app.MapHealthChecks("/health");
await app.RunAsync();

public partial class Program;
