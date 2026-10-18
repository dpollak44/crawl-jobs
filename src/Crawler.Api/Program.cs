using Crawler.Api;
using Crawler.Api.Errors;
using Crawler.Api.Jobs;
using Crawler.Infrastructure;
using FluentValidation;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddCrawlerDefaults();

// Request log: method, path, status, duration. Never bodies or headers.
builder.Services.AddHttpLogging(o => o.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// Live progress: the worker announces "job X changed" on a fanout; the listener hands it to the relay, which reads
// the summary once and pushes it to every open stream for that job.
builder.Services.AddSingleton(sp => new ProgressRelay(async (jobId, ct) =>
{
    await using var scope = sp.CreateAsyncScope();
    return await JobEndpoints.LoadSummaryAsync(scope.ServiceProvider.GetRequiredService<Crawler.Infrastructure.Persistence.AppDbContext>(), jobId, ct);
}));
builder.Services.AddHostedService<ProgressListener>();
builder.Services.Configure<CrawlOptions>(builder.Configuration.GetSection(CrawlOptions.Section));
builder.Services.AddSingleton<IValidator<CreateJobRequest>>(sp =>
{
    var o = sp.GetRequiredService<IOptions<CrawlOptions>>().Value;
    return new CreateJobRequestValidator(o.MaxDepthLimit, o.MaxPagesLimit);
});

// The UI is served from its own address (static files at the edge in production), so the API must say which origins may call it.
var uiOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:3000", "http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(uiOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
await app.EnsureDatabaseAsync();

app.UseExceptionHandler();
app.UseHttpLogging();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(); // interactive API page at /swagger
app.MapHealthChecks("/health").WithTags("Ops");
app.MapJobEndpoints();

await app.RunAsync();

// Lets the integration tests reference the host via WebApplicationFactory<Program>.
public partial class Program;
