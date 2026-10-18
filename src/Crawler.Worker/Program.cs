using Crawler.Infrastructure;
using Crawler.Worker.Crawling;
using Crawler.Worker.Messaging;
using Microsoft.Extensions.Http.Resilience;
using Polly;

// The worker is a web host too, for one reason: a /health endpoint the orchestrator can probe.
var builder = WebApplication.CreateBuilder(args);
builder.AddCrawlerDefaults();
builder.Services.Configure<CrawlOptions>(builder.Configuration.GetSection(CrawlOptions.Section));

var crawl = builder.Configuration.GetSection(CrawlOptions.Section).Get<CrawlOptions>() ?? new CrawlOptions();

// Fetching other people's websites: per-attempt timeout, retries with backoff, total timeout; redirects handled in PageFetcher.
builder.Services.AddHttpClient<PageFetcher>(c =>
    {
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CrawlJobs/1.0 (+https://github.com/dpollak44/crawl-jobs)");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // PageFetcher follows redirects itself: same-host http/https hops allowed, final URL recorded, hops capped
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    .AddResilienceHandler("fetch", b => b.AddPipeline(FetchPipeline.Build(crawl))); // see FetchPipeline: retries + timeouts, no shared breaker

builder.Services.AddScoped<PageProcessor>();
builder.Services.AddHostedService<RabbitConsumer>();
builder.Services.AddHostedService<QueuedPageSweeper>();

var app = builder.Build();
app.MapHealthChecks("/health");
await app.RunAsync();

public partial class Program;
