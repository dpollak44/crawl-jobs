using Crawler.Domain.Model;

namespace Crawler.Domain.Tests;

public class ErrorTextTests
{
    [Fact]
    public void A_failure_reason_longer_than_its_column_is_cut_so_the_save_can_never_be_refused()
    {
        var page = CrawlPage.Discover(Guid.NewGuid(), new Uri("https://example.com/"), 0, null, DateTimeOffset.UtcNow);
        var job = CrawlJob.Create(new Uri("https://example.com/"), 1, 10, DateTimeOffset.UtcNow);
        var reason = new string('x', 5000);

        page.MarkFailed(reason, null, DateTimeOffset.UtcNow);
        job.MarkFailed(reason, DateTimeOffset.UtcNow);

        page.Error!.Length.ShouldBe(CrawlPage.MaxErrorLength);
        page.Error.ShouldEndWith("…");
        job.FailureReason!.Length.ShouldBe(CrawlJob.MaxReasonLength);
    }
}
