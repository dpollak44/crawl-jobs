using System.Net;
using System.Net.Http.Json;
using Crawler.Api.Jobs;
using Crawler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Api.Tests;

/// <summary>The live-progress stream: a snapshot on connect, a line per change, `done` at the end, then the stream closes.</summary>
public class JobStreamTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public JobStreamTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Stream_sends_the_snapshot_then_each_change_then_done_and_closes()
    {
        var create = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("stream.example.com", null, null));
        var jobId = (await create.Content.ReadFromJsonAsync<CreateJobResponse>())!.JobId;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{jobId}/events");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var lines = new List<string>();

        // 1. the snapshot arrives immediately
        lines.Add((await reader.ReadLineAsync(cts.Token))!);
        lines[0].ShouldStartWith("data: ");
        lines[0].ShouldContain("\"status\":\"Pending\"");

        // 2. the worker (simulated) runs and completes the job, announcing "go look" after each change as the real worker does
        var relay = _factory.Services.GetRequiredService<ProgressRelay>();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.Jobs.SingleAsync(j => j.Id == jobId, cts.Token);
            var seed = await db.Pages.SingleAsync(p => p.JobId == jobId, cts.Token);
            job.MarkRunning(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cts.Token);
            (await relay.NotifyAsync(jobId, cts.Token)).ShouldBeTrue("the open stream should be subscribed");
            // Observe the Running line before completing: the relay keeps only the latest summary per stream, so
            // the contract is "each change that the reader has not yet consumed", not "every change ever".
            string? runningLine;
            do
            {
                runningLine = await reader.ReadLineAsync(cts.Token);
                runningLine.ShouldNotBeNull("the stream ended before reporting Running");
            }
            while (!runningLine.Contains("\"status\":\"Running\"", StringComparison.Ordinal));
            lines.Add(runningLine);
            seed.MarkFetched(new Uri("https://stream.example.com/"), 200, 0, 0, 0m, DateTimeOffset.UtcNow);
            job.MarkCompleted(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cts.Token);
            await relay.NotifyAsync(jobId, cts.Token);
        }

        // 3. read until the stream ends
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        var dataLines = lines.Where(l => l.StartsWith("data: {\"jobId\"", StringComparison.Ordinal)).ToList();
        dataLines.Count.ShouldBeGreaterThanOrEqualTo(2);
        dataLines.ShouldContain(l => l.Contains("\"status\":\"Running\""));
        dataLines.Last().ShouldContain("\"status\":\"Completed\"");
        lines.ShouldContain("event: done");
    }

    [Fact]
    public async Task Stream_for_an_unknown_job_is_404()
    {
        var response = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}/events");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
