using System.Net.Http.Json;
using Crawler.Api.Jobs;
using Crawler.Infrastructure.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crawler.Api.Tests;

/// <summary>Focused regression supplied by the independent (Codex) review; assertions kept as supplied.</summary>
public class AuditReviewTests
{
    private sealed class BrokenPublisher : IPagePublisher
    {
        public Task PublishAsync(CrawlPageMessage message, CancellationToken ct) => throw new InvalidOperationException("Audit: broker unavailable");
    }

    [Fact]
    public async Task Scheduling_failure_settles_the_seed_so_the_UI_can_stop_waiting()
    {
        using var factory = new ApiFactory();
        using var broken = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IPagePublisher>();
            s.AddSingleton<IPagePublisher, BrokenPublisher>();
        }));
        using var client = broken.CreateClient();

        var response = await client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("https://audit.example.com", null, null));
        var id = (await response.Content.ReadFromJsonAsync<CreateJobResponse>())!.JobId;
        var job = (await client.GetFromJsonAsync<JobSummary>($"/api/jobs/{id}"))!;

        job.Status.ShouldBe("Failed");
        job.FailureReason.ShouldNotBeNull();
        job.Progress.Queued.ShouldBe(0, "both the SSE stream and polling wait for Queued == 0; the sweeper excludes Failed jobs");
    }
}
