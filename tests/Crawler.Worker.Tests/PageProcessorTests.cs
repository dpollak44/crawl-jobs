using Crawler.Domain.Model;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Crawling;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Worker.Tests;

/// <summary>
/// The integration-style test the brief asks for: the worker's whole page step against local HTML fixtures,
/// a fake internet and an in-memory queue. The site: home → about, products, contact(404), brochure.pdf,
/// offsite partner; about → team; products → widget, about (already known).
/// </summary>
public class PageProcessorTests : IDisposable
{
    private readonly WorkerHarness _h = new();

    public PageProcessorTests()
    {
        _h.Site
            .Html("https://shop.test/", "home.html")
            .Html("https://shop.test/about", "about.html")
            .Html("https://shop.test/products", "products.html")
            .Html("https://shop.test/team", "leaf.html")
            .Html("https://shop.test/widget", "leaf.html")
            .Reply("https://shop.test/brochure.pdf", System.Net.HttpStatusCode.OK, "application/pdf", "%PDF-1.4")
            .Reply("https://shop.test/contact", System.Net.HttpStatusCode.NotFound);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task A_whole_crawl_completes_with_the_right_tree_and_ratios()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/", maxDepth: 2);

        var processed = await _h.DrainAsync();

        await using var db = _h.Db();
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        var pages = await db.Pages.Where(p => p.JobId == job.Id).OrderBy(p => p.Depth).ThenBy(p => p.Url).ToListAsync();

        saved.Status.ShouldBe(JobStatus.Completed);
        saved.StartedAt.ShouldNotBeNull();
        saved.CompletedAt.ShouldNotBeNull();
        processed.ShouldBe(pages.Count); // one message per page, every page handled exactly once
        _h.Announcer.Announced.Count(id => id == job.Id).ShouldBe(pages.Count); // one "go look" per settled page

        pages.Select(p => (p.Url, p.Depth, p.Status)).ShouldBe(
        [
            ("https://shop.test/", 0, PageStatus.Fetched),
            ("https://shop.test/about", 1, PageStatus.Fetched),
            ("https://shop.test/brochure.pdf", 1, PageStatus.Skipped),
            ("https://shop.test/contact", 1, PageStatus.Failed),
            ("https://shop.test/products", 1, PageStatus.Fetched),
            ("https://shop.test/team", 2, PageStatus.Fetched),
            ("https://shop.test/widget", 2, PageStatus.Fetched),
        ]);

        var home = pages.Single(p => p.Depth == 0);
        home.OutgoingLinks.ShouldBe(5);          // about, products, contact, brochure.pdf, partner (mailto ignored, self ignored)
        home.InternalLinks.ShouldBe(4);
        home.DomainLinkRatio.ShouldBe(0.8m);

        var products = pages.Single(p => p.Url == "https://shop.test/products");
        products.OutgoingLinks.ShouldBe(2);      // widget, about
        products.DomainLinkRatio.ShouldBe(1m);

        // "about" was first discovered from home, not from products: one parent, one tree shape.
        pages.Single(p => p.Url == "https://shop.test/about").ParentPageId.ShouldBe(home.Id);
        pages.Single(p => p.Url == "https://shop.test/widget").ParentPageId.ShouldBe(products.Id);

        // The off-site link is recorded as an edge but never became a page.
        var edges = await db.Links.Where(l => l.JobId == job.Id).ToListAsync();
        edges.ShouldContain(e => e.ChildUrl == "https://partner.example.org/" && !e.IsInternal);
        pages.ShouldNotContain(p => p.Url.Contains("partner"));
    }

    [Fact]
    public async Task Delivering_the_same_message_twice_creates_no_duplicate_rows()
    {
        var (job, seed) = await _h.StartJobAsync("https://shop.test/", maxDepth: 1);
        var seedMessage = _h.Queue.Pending.Dequeue();

        (await _h.ProcessAsync(seedMessage)).Outcome.ShouldBe(ProcessOutcome.Done);
        await using (var db = _h.Db())
        {
            var pagesAfterFirst = await db.Pages.CountAsync(p => p.JobId == job.Id);
            var linksAfterFirst = await db.Links.CountAsync(l => l.JobId == job.Id);
            var publishedAfterFirst = _h.Queue.AllPublished.Count;

            (await _h.ProcessAsync(seedMessage)).Outcome.ShouldBe(ProcessOutcome.Done);

            (await db.Pages.CountAsync(p => p.JobId == job.Id)).ShouldBe(pagesAfterFirst);
            (await db.Links.CountAsync(l => l.JobId == job.Id)).ShouldBe(linksAfterFirst);
            _h.Site.Requests.ShouldBe(1); // the page was not fetched again

            // The duplicate re-scheduled the still-queued children (harmless, and self-healing if a publish was lost).
            _h.Queue.AllPublished.Count.ShouldBeGreaterThan(publishedAfterFirst);
            _h.Queue.AllPublished.Skip(publishedAfterFirst).ShouldAllBe(m => m.JobId == job.Id && m.Depth == 1);
        }

        seed.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Depth_limit_stops_discovery_but_still_records_links()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/", maxDepth: 0);

        await _h.DrainAsync();

        await using var db = _h.Db();
        (await db.Pages.CountAsync(p => p.JobId == job.Id)).ShouldBe(1);
        (await db.Links.CountAsync(l => l.JobId == job.Id)).ShouldBe(5);
        (await db.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task Page_cap_is_respected()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/", maxDepth: 2, maxPages: 3);

        await _h.DrainAsync();

        await using var db = _h.Db();
        (await db.Pages.CountAsync(p => p.JobId == job.Id)).ShouldBe(3);
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.PagesDiscovered.ShouldBe(3);
        saved.Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task A_seed_that_cannot_be_fetched_fails_the_job_with_the_reason()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/contact");

        await _h.DrainAsync();

        await using var db = _h.Db();
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.ShouldBe(JobStatus.Failed);
        saved.FailureReason.ShouldNotBeNull().ShouldContain("HTTP 404");
    }

    [Fact]
    public async Task A_redirect_off_the_site_is_recorded_as_skipped_not_crawled()
    {
        _h.Site.Redirect("https://shop.test/moved", "https://elsewhere.test/landing").Html("https://elsewhere.test/landing", "leaf.html");
        var (job, _) = await _h.StartJobAsync("https://shop.test/moved");

        await _h.DrainAsync();

        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(PageStatus.Skipped);
        page.Error.ShouldNotBeNull().ShouldContain("elsewhere.test");
        (await db.Links.CountAsync(l => l.JobId == job.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_redirect_on_the_same_host_is_followed_even_from_https_to_http_and_the_final_url_recorded()
    {
        _h.Site.Redirect("https://shop.test/old", "http://shop.test/new/").Html("http://shop.test/new/", "leaf.html");
        var (job, _) = await _h.StartJobAsync("https://shop.test/old");

        await _h.DrainAsync();

        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(PageStatus.Fetched);
        page.FinalUrl.ShouldBe("http://shop.test/new/");
        _h.Site.Requests.ShouldBe(2);
    }

    [Fact]
    public async Task A_redirect_loop_gives_up_after_the_cap()
    {
        _h.Site.Redirect("https://shop.test/a", "https://shop.test/b").Redirect("https://shop.test/b", "https://shop.test/a");
        _h.Options.MaxRedirects = 3;
        var (job, _) = await _h.StartJobAsync("https://shop.test/a");

        await _h.DrainAsync();

        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(PageStatus.Failed);
        page.Error.ShouldNotBeNull().ShouldContain("redirects");
        _h.Site.Requests.ShouldBe(4); // 1 + 3 hops
    }

    [Fact]
    public async Task Giving_up_on_a_page_after_exhausted_retries_still_lets_the_job_complete()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/", maxDepth: 1);
        var seedMessage = _h.Queue.Pending.Dequeue();
        await _h.ProcessAsync(seedMessage); // 4 children queued
        var last = _h.Queue.Pending.Dequeue();
        await _h.DrainAsync();          // the other 3 processed; `last` never arrives cleanly

        await using (var db = _h.Db())
        {
            (await db.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Running); // one page still Queued
        }

        await using (var db = _h.Db())
        {
            var processor = new PageProcessor(db, new PageFetcher(new HttpClient(_h.Site, false), Microsoft.Extensions.Options.Options.Create(_h.Options)), _h.Queue, _h.Announcer,
                Microsoft.Extensions.Options.Options.Create(_h.Options), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PageProcessor>.Instance);
            await processor.GiveUpAsync(last with { Attempt = 3 }, "database unreachable", CancellationToken.None);
        }

        await using var check = _h.Db();
        var page = await check.Pages.SingleAsync(p => p.Id == last.PageId);
        page.Status.ShouldBe(PageStatus.Failed);
        page.Error.ShouldNotBeNull().ShouldContain("Gave up after 3 attempts");
        (await check.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task The_sweeper_republishes_pages_left_queued_too_long_on_running_jobs_only()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/", maxDepth: 1);
        await _h.ProcessAsync(_h.Queue.Pending.Dequeue()); // seed fetched; 4 children Queued, 4 messages pending
        var (doneJob, _) = await _h.StartJobAsync("https://shop.test/team");
        await _h.DrainAsync();                              // second job completes; first job's children were drained too
        _h.Queue.AllPublished.Clear();

        // Pretend the first job's children never got their messages: put two of them back to Queued, one old, one fresh.
        Guid oldPage, freshPage;
        await using (var db = _h.Db())
        {
            var children = await db.Pages.Where(p => p.JobId == job.Id && p.Depth == 1).OrderBy(p => p.Url).Take(2).ToListAsync();
            oldPage = children[0].Id;
            freshPage = children[1].Id;
            await db.Database.ExecuteSqlRawAsync("DELETE FROM links WHERE \"ParentPageId\" = {0}", oldPage); // a never-fetched page has no links yet
            await db.Database.ExecuteSqlRawAsync("UPDATE pages SET \"Status\" = 'Queued', \"LastQueuedAt\" = {0} WHERE \"Id\" = {1}", (_h.Now - TimeSpan.FromMinutes(10)).UtcTicks, oldPage);
            await db.Database.ExecuteSqlRawAsync("UPDATE pages SET \"Status\" = 'Queued', \"LastQueuedAt\" = {0} WHERE \"Id\" = {1}", _h.Now.UtcTicks, freshPage);
            await db.Database.ExecuteSqlRawAsync("UPDATE jobs SET \"Status\" = 'Running' WHERE \"Id\" = {0}", job.Id);
            // A page on a completed job must never be re-published, however old.
            var donePage = await db.Pages.SingleAsync(p => p.JobId == doneJob.Id);
            await db.Database.ExecuteSqlRawAsync("UPDATE pages SET \"Status\" = 'Queued', \"LastQueuedAt\" = {0} WHERE \"Id\" = {1}", (_h.Now - TimeSpan.FromMinutes(10)).UtcTicks, donePage.Id);
        }

        int republished;
        await using (var db = _h.Db())
        {
            republished = await QueuedPageSweeper.SweepOnceAsync(db, _h.Queue, _h.Now, TimeSpan.FromMinutes(2), CancellationToken.None);
        }

        republished.ShouldBe(1);
        _h.Queue.AllPublished.Single().PageId.ShouldBe(oldPage);

        // A second sweep in the same minute sends nothing: the page was stamped when it was re-published.
        await using (var db = _h.Db())
        {
            (await QueuedPageSweeper.SweepOnceAsync(db, _h.Queue, _h.Now, TimeSpan.FromMinutes(2), CancellationToken.None)).ShouldBe(0);
        }

        // And the re-published message completes the job through the normal path.
        await _h.DrainAsync();
        await using var check = _h.Db();
        (await check.Pages.SingleAsync(p => p.Id == oldPage)).Status.ShouldBe(PageStatus.Fetched);
        (await check.Pages.CountAsync(p => p.JobId == job.Id)).ShouldBe(5); // no duplicate rows
    }

    [Fact]
    public async Task The_same_page_spelled_with_http_or_www_becomes_one_page_row()
    {
        _h.Site.Html("https://shop.test/spellings", "spellings.html").Html("https://shop.test/x", "leaf.html");
        var (job, _) = await _h.StartJobAsync("https://shop.test/spellings", maxDepth: 1);

        await _h.DrainAsync();

        await using var db = _h.Db();
        var children = await db.Pages.Where(p => p.JobId == job.Id && p.Depth == 1).Select(p => p.Url).ToListAsync();
        children.ShouldBe(["https://shop.test/x"]);                       // http://, www., and https:// spellings collapsed into the job's own
        (await db.Links.CountAsync(l => l.JobId == job.Id)).ShouldBe(3); // but every link as written is still recorded as an edge
        (await db.Pages.SingleAsync(p => p.Url == "https://shop.test/spellings")).DomainLinkRatio.ShouldBe(1m);
    }

    [Fact]
    public async Task A_site_that_never_answers_is_recorded_as_unreachable_not_retried_through_the_queue()
    {
        _h.Options.FetchTimeoutSeconds = 1;
        _h.Options.FetchRetries = 0;
        _h.Site.Hang("https://shop.test/slow", TimeSpan.FromSeconds(5));
        var (job, _) = await _h.StartJobAsync("https://shop.test/slow");

        var result = await _h.ProcessAsync(_h.Queue.Pending.Dequeue());

        result.Outcome.ShouldBe(ProcessOutcome.Done); // the site's problem, not ours: no queue retry
        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(PageStatus.Failed);
        page.Error.ShouldNotBeNull().ShouldContain("Timed out");
        (await db.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Failed);
    }

    [Fact]
    public async Task A_server_that_sends_headers_but_never_finishes_the_body_is_recorded_as_unreachable()
    {
        _h.Options.FetchTimeoutSeconds = 1;
        _h.Options.FetchRetries = 0;
        _h.Site.Drip("https://shop.test/drip");
        var (job, _) = await _h.StartJobAsync("https://shop.test/drip");

        var result = await _h.ProcessAsync(_h.Queue.Pending.Dequeue());

        result.Outcome.ShouldBe(ProcessOutcome.Done);
        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(PageStatus.Failed);
        page.Error.ShouldNotBeNull().ShouldContain("reading the page body");
    }

    [Fact]
    public async Task A_message_for_an_unknown_job_is_poison()
    {
        var result = await _h.ProcessAsync(new CrawlPageMessage(Guid.NewGuid(), Guid.NewGuid(), "https://shop.test/", 0));

        result.Outcome.ShouldBe(ProcessOutcome.Poison);
        result.Reason.ShouldNotBeNull().ShouldContain("does not exist");
    }

    [Fact]
    public async Task A_canceled_job_stops_fetching_its_remaining_pages()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/", maxDepth: 1);
        var seedMessage = _h.Queue.Pending.Dequeue();
        await _h.ProcessAsync(seedMessage); // discovers 4 children

        await using (var db = _h.Db())
        {
            var j = await db.Jobs.SingleAsync(x => x.Id == job.Id);
            j.Cancel(DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync();
        }

        var requestsBefore = _h.Site.Requests;
        await _h.DrainAsync();

        _h.Site.Requests.ShouldBe(requestsBefore); // nothing else was fetched
        await using var check = _h.Db();
        (await check.Pages.CountAsync(p => p.JobId == job.Id && p.Status == PageStatus.Skipped)).ShouldBe(4);
        (await check.Jobs.SingleAsync(x => x.Id == job.Id)).Status.ShouldBe(JobStatus.Canceled);
    }
}
