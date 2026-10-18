using Crawler.Domain.Crawling;

namespace Crawler.Domain.Tests;

public class SiteScopeTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/a", true)]
    [InlineData("example.com", "http://EXAMPLE.com/a", true)]      // scheme and case do not matter
    [InlineData("example.com", "https://example.com./a", true)]    // a trailing dot is the same name
    [InlineData("example.com", "https://www.example.com/a", false)] // the brief: the starting domain is the exact initial host
    [InlineData("www.example.com", "https://example.com/a", false)]
    [InlineData("example.com", "https://blog.example.com/a", false)]
    [InlineData("example.com", "https://example.org/a", false)]
    [InlineData("example.com", "https://notexample.com/a", false)]
    public void Same_site_means_the_exact_starting_host_ignoring_scheme_and_case(string startHost, string url, bool expected)
    {
        new SiteScope(startHost).Contains(new Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("example.com", "https://www.example.com/a")]
    [InlineData("www.example.com", "https://example.com/a")]
    public void Assignment_starting_domain_is_the_exact_initial_host(string host, string link)
        => new SiteScope(host).Contains(new Uri(link)).ShouldBeFalse();

    [Theory]
    [InlineData("https://blog.example.com/a", true)]
    [InlineData("https://docs.api.example.com/a", true)]
    [InlineData("https://www.example.com/a", true)]   // with the switch, www. is just another subdomain
    [InlineData("https://example.org/a", false)]
    [InlineData("https://notexample.com/a", false)]
    public void Subdomains_count_as_inside_only_when_switched_on(string url, bool expected)
    {
        new SiteScope("example.com", includeSubdomains: true).Contains(new Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("http://shop.test/x?q=1", "https://shop.test/x?q=1")]        // other scheme: rewritten to the job's
    [InlineData("http://shop.test:8080/x", "https://shop.test:8080/x")]      // a non-default port survives
    [InlineData("HTTP://SHOP.test/x", "https://shop.test/x")]
    [InlineData("https://shop.test/x", "https://shop.test/x")]               // already the job's spelling: unchanged
    [InlineData("https://www.shop.test/x", "https://www.shop.test/x")]       // a different host is off-site: never touched
    [InlineData("https://elsewhere.test/x", "https://elsewhere.test/x")]     // off-site: never touched
    public void Same_site_links_are_rewritten_to_the_jobs_own_scheme_so_one_page_has_one_key(string link, string expected)
    {
        var seed = new Uri("https://shop.test/");

        new SiteScope(seed.Host).Canonicalize(new Uri(link), seed).ToString().ShouldBe(expected);
    }
}
