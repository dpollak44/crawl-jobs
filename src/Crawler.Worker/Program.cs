using Crawler.Infrastructure;

// The worker is a web host too, for one reason: a /health endpoint the orchestrator can probe.
var builder = WebApplication.CreateBuilder(args);
builder.AddCrawlerDefaults();

var app = builder.Build();
app.MapHealthChecks("/health");
await app.RunAsync();

public partial class Program;
