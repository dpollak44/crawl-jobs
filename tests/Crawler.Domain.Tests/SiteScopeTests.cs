using Crawler.Domain.Crawling;

namespace Crawler.Domain.Tests;

public class SiteScopeTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/a", true)]
    [InlineData("example.com", "https://www.example.com/a", true)]
    [InlineData("www.example.com", "https://example.com/a", true)]
    [InlineData("example.com", "http://EXAMPLE.com/a", true)]
    [InlineData("example.com", "https://blog.example.com/a", false)]
    [InlineData("example.com", "https://example.org/a", false)]
    [InlineData("example.com", "https://notexample.com/a", false)]
    public void Same_site_means_same_host_ignoring_www_and_scheme(string startHost, string url, bool expected)
    {
        new SiteScope(startHost).Contains(new Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://blog.example.com/a", true)]
    [InlineData("https://docs.api.example.com/a", true)]
    [InlineData("https://example.org/a", false)]
    [InlineData("https://notexample.com/a", false)]
    public void Subdomains_count_as_inside_only_when_switched_on(string url, bool expected)
    {
        new SiteScope("example.com", includeSubdomains: true).Contains(new Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("http://shop.test/x?q=1", "https://shop.test/x?q=1")]   // other scheme: rewritten to the job's
    [InlineData("https://www.shop.test/x", "https://shop.test/x")]       // www.: rewritten to the job's host
    [InlineData("HTTP://WWW.shop.test/x", "https://shop.test/x")]
    [InlineData("https://shop.test/x", "https://shop.test/x")]           // already the job's spelling: unchanged
    [InlineData("https://elsewhere.test/x", "https://elsewhere.test/x")] // off-site: never touched
    [InlineData("https://blog.shop.test/x", "https://blog.shop.test/x")] // a subdomain is off-site by default: never touched
    public void Same_site_links_are_rewritten_to_the_jobs_own_scheme_and_host_so_one_page_has_one_key(string link, string expected)
    {
        var seed = new Uri("https://shop.test/");

        new SiteScope(seed.Host).Canonicalize(new Uri(link), seed).ToString().ShouldBe(expected);
    }
}
