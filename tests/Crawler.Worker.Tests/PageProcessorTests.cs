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
        var (job, _) = await _h.StartJobAsync("https://shop.test/", maxDepth: 1);
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

        var announcedBefore = _h.Announcer.Announced.Count;
        await using (var db = _h.Db())
        {
            await _h.Processor(db).GiveUpAsync(last with { Attempt = 3 }, "database unreachable", CancellationToken.None);
        }

        await using var check = _h.Db();
        var page = await check.Pages.SingleAsync(p => p.Id == last.PageId);
        page.Status.ShouldBe(PageStatus.Failed);
        page.Error.ShouldNotBeNull().ShouldContain("Gave up after 3 attempts");
        (await check.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Completed);
        _h.Announcer.Announced.Count.ShouldBe(announcedBefore + 1); // the screen is told the job finished
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
    public async Task A_message_for_an_unknown_job_or_an_unknown_page_is_poison()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/");

        var noJob = await _h.ProcessAsync(new CrawlPageMessage(Guid.NewGuid(), Guid.NewGuid(), "https://shop.test/", 0));
        var noPage = await _h.ProcessAsync(new CrawlPageMessage(job.Id, Guid.NewGuid(), "https://shop.test/", 0));

        noJob.Outcome.ShouldBe(ProcessOutcome.Poison);
        noJob.Reason.ShouldNotBeNull().ShouldContain("does not exist");
        noPage.Outcome.ShouldBe(ProcessOutcome.Poison);
        noPage.Reason.ShouldNotBeNull().ShouldContain("does not exist");
        _h.Site.Requests.ShouldBe(0);
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

    [Theory]
    [InlineData(1, PageStatus.Fetched, 2)] // one 503, then the page: fetched on the retry
    [InlineData(9, PageStatus.Failed, 3)]  // never recovers: 1 attempt + 2 retries, then recorded as the site's failure
    public async Task A_server_error_is_retried_by_the_http_policy_and_recorded_only_once_the_budget_is_spent(int failures, PageStatus expected, int requests)
    {
        _h.Options.FetchRetries = 2;
        _h.Site.Flaky("https://shop.test/flaky", failures, System.Net.HttpStatusCode.ServiceUnavailable, "leaf.html");
        var (job, _) = await _h.StartJobAsync("https://shop.test/flaky");

        var result = await _h.ProcessAsync(_h.Queue.Pending.Dequeue());

        result.Outcome.ShouldBe(ProcessOutcome.Done); // HTTP retries happen inside the fetch; the queue never sees them
        _h.Site.Requests.ShouldBe(requests);
        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(expected);
        if (expected == PageStatus.Failed)
        {
            page.Error.ShouldNotBeNull().ShouldContain("HTTP 503");
        }
    }

    [Theory]
    [InlineData(true, "Page is 4 KB; limit is 1 KB.")]           // the server declares Content-Length: refused before a byte of body is read
    [InlineData(false, "Page exceeds the 1 KB limit; not parsed.")] // no Content-Length: refused the moment the cap is crossed while reading
    public async Task A_page_over_the_body_limit_is_skipped_with_the_reason_and_never_parsed(bool declaresLength, string reason)
    {
        _h.Options.MaxBodyBytes = 1024;
        var big = "<html><body>" + new string('x', 4096) + "<a href='/about'>About</a></body></html>";
        if (declaresLength)
        {
            _h.Site.Reply("https://shop.test/big", System.Net.HttpStatusCode.OK, "text/html", big);
        }
        else
        {
            _h.Site.HtmlWithoutLength("https://shop.test/big", big);
        }

        var (job, _) = await _h.StartJobAsync("https://shop.test/big");

        await _h.DrainAsync();

        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(PageStatus.Skipped);
        page.Error.ShouldBe(reason);
        (await db.Links.CountAsync(l => l.JobId == job.Id)).ShouldBe(0);                      // the link inside was never seen
        (await db.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Failed); // it was the starting page, so the job says why
    }

    [Fact]
    public async Task A_row_already_written_by_another_worker_is_skipped_and_the_page_still_completes_in_one_pass()
    {
        var (job, seed) = await _h.StartJobAsync("https://shop.test/", maxDepth: 1);
        await using (var db = _h.Db())
        {
            // Another worker got there first with one of this page's edges and one of its children.
            db.Links.Add(PageLink.Create(job.Id, seed.Id, new Uri("https://shop.test/about"), isInternal: true));
            db.Pages.Add(CrawlPage.Discover(job.Id, new Uri("https://shop.test/products"), 1, seed.Id, _h.Now));
            await db.SaveChangesAsync();
        }

        var result = await _h.ProcessAsync(_h.Queue.Pending.Dequeue());

        result.Outcome.ShouldBe(ProcessOutcome.Done); // no refetch, no attempt spent
        _h.Site.Requests.ShouldBe(1);
        await using var check = _h.Db();
        (await check.Pages.SingleAsync(p => p.Id == seed.Id)).Status.ShouldBe(PageStatus.Fetched);
        (await check.Links.CountAsync(l => l.ParentPageId == seed.Id)).ShouldBe(5);                       // the pre-existing edge once, the other four added
        (await check.Pages.CountAsync(p => p.JobId == job.Id && p.Depth == 1)).ShouldBe(4);              // products (theirs) + about, contact, brochure.pdf (ours)
        _h.Queue.Pending.Select(m => m.Url).ShouldNotContain("https://shop.test/products");               // we never re-schedule their row
    }

    [Fact]
    public async Task A_stale_copy_of_a_job_cannot_overwrite_a_final_status()
    {
        var (job, _) = await _h.StartJobAsync("https://shop.test/");
        await using var workerDb = _h.Db();
        await using var apiDb = _h.Db();
        var workersCopy = await workerDb.Jobs.SingleAsync(j => j.Id == job.Id);
        var apisCopy = await apiDb.Jobs.SingleAsync(j => j.Id == job.Id);

        apisCopy.Cancel(_h.Now).IsSuccess.ShouldBeTrue();
        await apiDb.SaveChangesAsync();                             // the user canceled while the worker was busy
        workersCopy.MarkCompleted(_h.Now).IsSuccess.ShouldBeTrue(); // the worker, none the wiser, tries to finish it

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => workerDb.SaveChangesAsync());
        await using var check = _h.Db();
        (await check.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Canceled);
    }
    [Fact]
    public async Task A_redirect_to_a_private_address_is_refused_before_anything_is_read()
    {
        _h.Site.Redirect("https://shop.test/leak", "http://169.254.169.254/latest/meta-data");
        var (job, _) = await _h.StartJobAsync("https://shop.test/leak");

        await _h.DrainAsync();

        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(PageStatus.Skipped);
        page.Error.ShouldNotBeNull().ShouldContain("private");
        _h.Site.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task A_give_up_on_the_starting_page_never_turns_a_canceled_job_into_a_failed_one()
    {
        var (job, seed) = await _h.StartJobAsync("https://shop.test/");
        var message = _h.Queue.Pending.Dequeue();
        await using (var db = _h.Db())
        {
            var j = await db.Jobs.SingleAsync(x => x.Id == job.Id);
            j.Cancel(_h.Now).IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync();
        }

        await using (var db = _h.Db())
        {
            var processor = new PageProcessor(db, new PageFetcher(new HttpClient(_h.Site, false), Microsoft.Extensions.Options.Options.Create(_h.Options)), _h.Queue, _h.Announcer,
                Microsoft.Extensions.Options.Options.Create(_h.Options), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PageProcessor>.Instance);
            await processor.GiveUpAsync(message with { Attempt = 3 }, "database unreachable", CancellationToken.None);
        }

        await using var check = _h.Db();
        (await check.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Canceled);
        (await check.Pages.SingleAsync(p => p.Id == seed.Id)).Status.ShouldBe(PageStatus.Failed);
    }

    [Fact]
    public async Task A_starting_page_on_a_private_address_is_never_requested()
    {
        var (job, _) = await _h.StartJobAsync("http://10.0.0.5/admin");

        await _h.DrainAsync();

        await using var db = _h.Db();
        var page = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        page.Status.ShouldBe(PageStatus.Skipped);
        page.Error.ShouldNotBeNull().ShouldContain("Not a public web address");
        _h.Site.Requests.ShouldBe(0);
        (await db.Jobs.SingleAsync(j => j.Id == job.Id)).Status.ShouldBe(JobStatus.Failed);
    }

    [Fact]
    public async Task A_page_whose_header_names_no_charset_is_decoded_by_its_own_meta_declaration()
    {
        // windows-1252 bytes: "café" has 0xE9 for é. Decoded as UTF-8 by mistake it would become a replacement character.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var html = "<html><head><meta charset=\"windows-1252\"></head><body><a href=\"/caf\u00e9\">menu</a></body></html>";
        var bytes = System.Text.Encoding.GetEncoding(1252).GetBytes(html);
        _h.Site.Bytes("https://shop.test/menu", bytes).Html("https://shop.test/café", "leaf.html");
        var (job, _) = await _h.StartJobAsync("https://shop.test/menu", maxDepth: 1);

        await _h.DrainAsync();

        await using var db = _h.Db();
        var child = await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Depth == 1);
        child.Url.ShouldBe("https://shop.test/café"); // é survived because the page was decoded as it said; decoded as UTF-8 it would be garbage
        child.Status.ShouldBe(PageStatus.Fetched);
    }

    [Fact]
    public void The_sweeper_default_matches_the_documented_five_minutes()
    {
        new Crawler.Infrastructure.CrawlOptions().StuckAfterSeconds.ShouldBe(300);
        new Crawler.Infrastructure.CrawlOptions().SweepIntervalSeconds.ShouldBe(60);
    }
}
