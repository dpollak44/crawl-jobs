using System.Net;
using System.Net.Http.Json;
using Crawler.Api.Jobs;
using Crawler.Domain.Model;
using Crawler.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Api.Tests;

public class JobEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public JobEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Creating_a_job_records_it_as_pending_with_its_seed_page_and_publishes_one_message()
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("HTTPS://Example.com/Docs/#top", null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = (await response.Content.ReadFromJsonAsync<CreateJobResponse>()).ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe($"/api/jobs/{created.JobId}");

        var summary = (await _client.GetFromJsonAsync<JobSummary>($"/api/jobs/{created.JobId}")).ShouldNotBeNull();
        summary.Status.ShouldBe("Pending");
        summary.Url.ShouldBe("https://example.com/Docs/"); // normalized: lowercase host, fragment gone, path kept as written
        summary.MaxDepth.ShouldBe(2);
        summary.MaxPages.ShouldBe(200);
        summary.Progress.Queued.ShouldBe(1);
        summary.Progress.Discovered.ShouldBe(1);

        var message = _factory.Publisher.Published.Where(m => m.JobId == created.JobId).ShouldHaveSingleItem(); // the factory is shared by the class
        message.JobId.ShouldBe(created.JobId);
        message.Depth.ShouldBe(0);
        message.Url.ShouldBe("https://example.com/Docs/");
    }

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  www.Example.com/Docs/  ", "https://www.example.com/Docs/")]
    [InlineData("http://example.com", "http://example.com/")]
    public async Task An_address_without_a_scheme_is_accepted_and_assumed_https(string typed, string stored)
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest(typed, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = (await response.Content.ReadFromJsonAsync<CreateJobResponse>()).ShouldNotBeNull();
        (await _client.GetFromJsonAsync<JobSummary>($"/api/jobs/{created.JobId}"))!.Url.ShouldBe(stored);
    }

    [Theory]
    [InlineData("", "Enter the address")]
    [InlineData("not a url", "does not look like a website address")]
    [InlineData("ftp://example.com", "does not look like a website address")]
    [InlineData("localhost", "does not look like a website address")]
    public async Task A_bad_address_is_refused_with_a_message_a_person_can_act_on(string url, string expectedText)
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest(url, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(expectedText);
    }

    [Fact]
    public async Task Depth_and_page_cap_are_bounded()
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("https://example.com", 99, 0));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Depth must be between");
        body.ShouldContain("Page limit must be between");
    }

    [Fact]
    public async Task Unknown_job_is_404_for_summary_tree_and_cancel()
    {
        var id = Guid.NewGuid();

        (await _client.GetAsync($"/api/jobs/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/jobs/{id}/tree")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.PostAsync($"/api/jobs/{id}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task History_lists_newest_first_and_pages()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        for (var i = 1; i <= 3; i++)
        {
            await client.PostAsJsonAsync("/api/jobs", new CreateJobRequest($"https://site{i}.example.com", null, null));
        }

        var page1 = (await client.GetFromJsonAsync<Page<JobSummary>>("/api/jobs?page=1&pageSize=2")).ShouldNotBeNull();
        var page2 = (await client.GetFromJsonAsync<Page<JobSummary>>("/api/jobs?page=2&pageSize=2")).ShouldNotBeNull();

        page1.Total.ShouldBe(3);
        page1.Items.Count.ShouldBe(2);
        page1.Items[0].Url.ShouldBe("https://site3.example.com/");
        page1.Items[1].Url.ShouldBe("https://site2.example.com/");
        page2.Items.Single().Url.ShouldBe("https://site1.example.com/");
    }

    [Fact]
    public async Task Paging_values_out_of_range_are_clamped_and_non_numbers_refused()
    {
        var page = (await _client.GetFromJsonAsync<Page<JobSummary>>("/api/jobs?page=0&pageSize=500")).ShouldNotBeNull();

        page.PageNumber.ShouldBe(1);
        page.PageSize.ShouldBe(100); // Paging:MaxPageSize in appsettings.json
        (await _client.GetAsync("/api/jobs?page=abc")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task If_the_broker_is_down_when_a_job_is_created_the_job_is_failed_with_the_reason_not_left_pending()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        factory.Publisher.FailNextWith = new IOException("connection refused");

        var response = await client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("https://outage.example.com", null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted); // the job exists and can be opened
        var jobId = (await response.Content.ReadFromJsonAsync<CreateJobResponse>())!.JobId;
        var summary = (await client.GetFromJsonAsync<JobSummary>($"/api/jobs/{jobId}")).ShouldNotBeNull();
        summary.Status.ShouldBe("Failed");
        summary.FailureReason.ShouldNotBeNull().ShouldContain("message broker is unavailable");
        factory.Publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tree_nests_pages_under_their_first_discovered_parent_and_the_summary_counts_the_same_rows()
    {
        var create = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("https://tree.example.com", null, null));
        var jobId = (await create.Content.ReadFromJsonAsync<CreateJobResponse>())!.JobId;

        // Simulate what the worker will write: two children under the seed, one grandchild.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var seed = db.Pages.Single(p => p.JobId == jobId);
            var now = DateTimeOffset.UtcNow;
            var a = CrawlPage.Discover(jobId, new Uri("https://tree.example.com/a"), 1, seed.Id, now);
            var b = CrawlPage.Discover(jobId, new Uri("https://tree.example.com/b"), 1, seed.Id, now.AddMilliseconds(1));
            var a1 = CrawlPage.Discover(jobId, new Uri("https://tree.example.com/a/1"), 2, a.Id, now.AddMilliseconds(2));
            seed.MarkFetched(new Uri("https://tree.example.com/"), 200, outgoingLinks: 4, internalLinks: 2, ratio: 0.5m, now);
            db.Pages.AddRange(a, b, a1);
            db.SaveChanges();
        }

        var tree = (await _client.GetFromJsonAsync<TreeNode>($"/api/jobs/{jobId}/tree")).ShouldNotBeNull();

        tree.Url.ShouldBe("https://tree.example.com/");
        tree.Status.ShouldBe("Fetched");
        tree.DomainLinkRatio.ShouldBe(0.5m);
        tree.Children.Select(c => c.Url).ShouldBe(["https://tree.example.com/a", "https://tree.example.com/b"]);
        tree.Children[0].Children.Single().Url.ShouldBe("https://tree.example.com/a/1");
        tree.Children[0].Children.Single().Depth.ShouldBe(2);
        tree.Children[1].Children.ShouldBeEmpty();

        var summary = (await _client.GetFromJsonAsync<JobSummary>($"/api/jobs/{jobId}")).ShouldNotBeNull();
        summary.Progress.Fetched.ShouldBe(1);
        summary.Progress.Queued.ShouldBe(3);
        summary.Progress.Failed.ShouldBe(0);
    }

    [Fact]
    public async Task Cancel_works_once_and_then_refuses_with_a_reason()
    {
        var create = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("https://cancel.example.com", null, null));
        var jobId = (await create.Content.ReadFromJsonAsync<CreateJobResponse>())!.JobId;

        var first = await _client.PostAsync($"/api/jobs/{jobId}/cancel", null);
        var second = await _client.PostAsync($"/api/jobs/{jobId}/cancel", null);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JobSummary>())!.Status.ShouldBe("Canceled");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).ShouldContain("Canceled job cannot be canceled");
    }

    [Fact]
    public async Task A_starting_address_over_the_limit_gets_a_plain_message_not_a_500()
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("https://example.com/?t=" + new string('x', 2100), null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("too long to crawl");
    }

    [Theory]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://intranet.internal/")]
    [InlineData("http://[::1]/")]
    public async Task A_private_or_internal_starting_address_is_refused_with_a_plain_message(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest(url, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("private or internal");
    }

    [Fact]
    public async Task Cancel_replies_with_the_real_progress_counts()
    {
        var create = await _client.PostAsJsonAsync("/api/jobs", new CreateJobRequest("https://counts.example.com", null, null));
        var jobId = (await create.Content.ReadFromJsonAsync<CreateJobResponse>())!.JobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var seed = db.Pages.Single(p => p.JobId == jobId);
            seed.MarkFetched(new Uri("https://counts.example.com/"), 200, 2, 2, 1m, DateTimeOffset.UtcNow);
            db.Pages.Add(CrawlPage.Discover(jobId, new Uri("https://counts.example.com/a"), 1, seed.Id, DateTimeOffset.UtcNow));
            db.Pages.Add(CrawlPage.Discover(jobId, new Uri("https://counts.example.com/b"), 1, seed.Id, DateTimeOffset.UtcNow));
            db.SaveChanges();
        }

        var reply = await _client.PostAsync($"/api/jobs/{jobId}/cancel", null);

        reply.StatusCode.ShouldBe(HttpStatusCode.OK);
        var summary = (await reply.Content.ReadFromJsonAsync<JobSummary>())!;
        summary.Status.ShouldBe("Canceled");
        summary.Progress.Fetched.ShouldBe(1);
        summary.Progress.Queued.ShouldBe(2); // the screen never flashes "0 of N"
    }
}
