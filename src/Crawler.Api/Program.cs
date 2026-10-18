using Crawler.Api.Claims;
using Crawler.Api.Data;
using Crawler.Api.Errors;
using Crawler.Api.FileImports;
using Crawler.Api.Outbound;
using FluentValidation;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Logs: one JSON line per event outside Development (what Application Insights / any log shipper wants).
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders().AddJsonConsole();
}

// Request log: method, path, status, duration. Never bodies or headers (PHI).
builder.Services.AddHttpLogging(o => o.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateClaimRequestValidator>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=takehome.db"));
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

// Outbound integration: retries with backoff, per-attempt timeout and a circuit breaker come from the
// standard resilience handler; the numbers live in appsettings under "Resilience".
builder.Services.AddHttpClient<PayerClient>(c => c.BaseAddress = new Uri(builder.Configuration["Payer:BaseUrl"] ?? "http://localhost:9999/"))
    .AddStandardResilienceHandler()
    .Configure(builder.Configuration.GetSection("Resilience"));
builder.Services.AddScoped<OutboundDispatcher>();

var app = builder.Build();

// Take-home scale: create the schema on boot. A real service runs EF migrations as a release step.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();
app.UseHttpLogging();

app.MapOpenApi();
app.MapScalarApiReference(); // interactive API page at /scalar
app.MapHealthChecks("/health").WithTags("Ops");

app.MapClaimEndpoints();
app.MapImportEndpoints();
app.MapOutboundEndpoints();

await app.RunAsync();

// Lets the integration tests reference the host via WebApplicationFactory<Program>.
public partial class Program;
