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
}
