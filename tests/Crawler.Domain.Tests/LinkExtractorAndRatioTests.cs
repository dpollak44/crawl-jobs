using Crawler.Domain.Crawling;

namespace Crawler.Domain.Tests;

public class LinkExtractorAndRatioTests
{
    private static readonly Uri Page = new("https://www.example.com/products/");
    private static readonly SiteScope Scope = new("www.example.com");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Extracts_anchor_links_only_normalized_and_deduplicated_without_self_links()
    {
        var links = LinkExtractor.Extract(Fixture("products.html"), Page);

        links.Select(l => l.ToString()).ShouldBe(
        [
            "https://www.example.com/products/widget",
            "https://www.example.com/about",
            "https://www.example.com/pricing?plan=pro",
            "https://partner.example.org/",
            "https://docs.example.com/start",
        ]);
    }

    [Fact]
    public void Ratio_counts_links_that_stay_on_the_site_over_all_links()
    {
        var links = LinkExtractor.Extract(Fixture("products.html"), Page);

        var counts = DomainLinkRatio.Count(links, Scope);

        counts.Internal.ShouldBe(3);
        counts.Total.ShouldBe(5);
        counts.Ratio.ShouldBe(0.6m);
    }

    [Fact]
    public void Subdomain_switch_moves_docs_link_inside()
    {
        var links = LinkExtractor.Extract(Fixture("products.html"), Page);

        var counts = DomainLinkRatio.Count(links, new SiteScope("www.example.com", includeSubdomains: true));

        counts.Internal.ShouldBe(4);
        counts.Ratio.ShouldBe(0.8m);
    }

    [Fact]
    public void A_page_with_no_links_has_ratio_zero()
    {
        var links = LinkExtractor.Extract("<html><body><p>Nothing here.</p><a name='anchor-without-href'>x</a></body></html>", Page);

        links.ShouldBeEmpty();
        DomainLinkRatio.Count(links, Scope).Ratio.ShouldBe(0m);
    }

    [Fact]
    public void A_page_with_only_offsite_links_has_ratio_zero_and_only_onsite_links_has_ratio_one()
    {
        var offsite = LinkExtractor.Extract("<a href='https://a.org'>a</a><a href='https://b.org'>b</a>", Page);
        var onsite = LinkExtractor.Extract("<a href='/x'>x</a><a href='/y'>y</a>", Page);

        DomainLinkRatio.Count(offsite, Scope).Ratio.ShouldBe(0m);
        DomainLinkRatio.Count(onsite, Scope).Ratio.ShouldBe(1m);
    }

    [Fact]
    public void Base_href_changes_how_relative_links_resolve()
    {
        var links = LinkExtractor.Extract("<html><head><base href='https://www.example.com/shop/'></head><body><a href='cart'>c</a></body></html>", Page);

        links.Single().ToString().ShouldBe("https://www.example.com/shop/cart");
    }
}
