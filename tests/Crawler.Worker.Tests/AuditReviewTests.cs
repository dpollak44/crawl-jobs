using System.Net;
using Crawler.Domain.Crawling;
using Crawler.Domain.Model;
using Crawler.Infrastructure;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Crawling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Tests;

/// <summary>Focused regressions supplied by the independent (Codex) review; assertions kept as supplied.</summary>
public class AuditReviewTests
{
    private sealed class Handler(Func<int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(++Calls));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    [Theory]
    [InlineData(503, 2)]
    [InlineData(429, 2)]
    [InlineData(404, 1)]
    public async Task Transient_HTTP_status_retries_but_permanent_404_does_not(int firstStatus, int expectedCalls)
    {
        var options = new CrawlOptions { FetchRetries = 1, FetchTimeoutSeconds = 2 };
        using var handler = new Handler(n => new HttpResponseMessage(n == 1 ? (HttpStatusCode)firstStatus : HttpStatusCode.OK)
        { Content = new StringContent("<html>ok</html>", System.Text.Encoding.UTF8, "text/html") });
        using var http = new HttpClient(new ResilienceHandler(FetchPipeline.Build(options)) { InnerHandler = handler });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var result = await new PageFetcher(http, Options.Create(options)).FetchAsync(new Uri("https://audit.test/"), timeout.Token);

        handler.Calls.ShouldBe(expectedCalls);
        result.Outcome.ShouldBe(expectedCalls == 1 ? FetchOutcome.HttpError : FetchOutcome.Html);
    }

    [Theory]
    [InlineData(4, true, FetchOutcome.Html)]
    [InlineData(5, true, FetchOutcome.NotHtml)]
    [InlineData(5, false, FetchOutcome.NotHtml)]
    public async Task Body_limit_covers_known_unknown_and_exact_boundary(int size, bool knownLength, FetchOutcome expected)
    {
        using var handler = new Handler(_ =>
        {
            HttpContent content = knownLength ? new ByteArrayContent(new byte[size]) : new UnknownLengthContent(new byte[size]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        var fetcher = new PageFetcher(http, Options.Create(new CrawlOptions { MaxBodyBytes = 4 }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        (await fetcher.FetchAsync(new Uri("https://audit.test/"), timeout.Token)).Outcome.ShouldBe(expected);
    }

    private sealed class ThrowOncePublisher(IPagePublisher inner) : IPagePublisher
    {
        private bool _thrown;
        public Task PublishAsync(CrawlPageMessage message, CancellationToken ct)
        {
            if (!_thrown) { _thrown = true; throw new InvalidOperationException("Audit: child publish interrupted after DB commit"); }
            return inner.PublishAsync(message, ct);
        }
    }

    [Fact]
    public async Task Redelivery_recovers_children_after_post_commit_publication_failure()
    {
        using var h = new WorkerHarness();
        h.Site.Html("https://shop.test/", "home.html");
        var (job, _) = await h.StartJobAsync("https://shop.test/", maxDepth: 1);
        var message = h.Queue.Pending.Dequeue();
        using (var http = new HttpClient(h.Site, false))
        await using (var db = h.Db())
        {
            var p = new PageProcessor(db, new PageFetcher(http, Options.Create(h.Options)), new ThrowOncePublisher(h.Queue), h.Announcer,
                Options.Create(h.Options), h.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<PageProcessor>.Instance);
            await Should.ThrowAsync<InvalidOperationException>(() => p.ProcessAsync(message, CancellationToken.None));
        }

        await using (var db = h.Db())
        {
            (await db.Pages.CountAsync(p => p.JobId == job.Id && p.Status == PageStatus.Queued)).ShouldBe(4);
        }

        (await h.ProcessAsync(message)).Outcome.ShouldBe(ProcessOutcome.Done);
        await h.DrainAsync();
        await using var final = h.Db();
        (await final.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Completed);
        (await final.Pages.CountAsync(p => p.JobId == job.Id)).ShouldBe(5);
        (await final.Pages.CountAsync(p => p.JobId == job.Id && p.Status == PageStatus.Queued)).ShouldBe(0);
    }

    private sealed class RefusingConnectHandler(AddressRefusal reason) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            // What SocketsHttpHandler does with an exception from its connect callback: wraps it.
            throw new HttpRequestException("connect failed", new AddressRefusedException(request.RequestUri!.Host, reason));
        }
    }

    [Theory]
    [InlineData(AddressRefusal.Private, FetchOutcome.OutOfScope, "private or internal")]
    [InlineData(AddressRefusal.NotFound, FetchOutcome.Unreachable, "could not be found")]
    public async Task A_refusal_from_the_connect_step_is_final_mapped_by_reason_and_never_retried(AddressRefusal reason, FetchOutcome expected, string text)
    {
        var options = new CrawlOptions { FetchRetries = 2, FetchTimeoutSeconds = 2 };
        using var handler = new RefusingConnectHandler(reason);
        using var http = new HttpClient(new ResilienceHandler(FetchPipeline.Build(options)) { InnerHandler = handler });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var result = await new PageFetcher(http, Options.Create(options)).FetchAsync(new Uri("https://looks-public.example.com/"), timeout.Token);

        result.Outcome.ShouldBe(expected);
        result.Error.ShouldNotBeNull().ShouldContain(text);
        handler.Calls.ShouldBe(1); // the retry policy left it alone
    }
}
