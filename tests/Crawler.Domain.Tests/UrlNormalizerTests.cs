using Crawler.Domain.Crawling;

namespace Crawler.Domain.Tests;

public class UrlNormalizerTests
{
    private static readonly Uri Page = new("https://www.example.com/docs/guide.html");

    private static string Normalize(string href, Uri? @base = null)
    {
        UrlNormalizer.TryNormalize(href, @base ?? Page, out var result).ShouldBeTrue($"'{href}' should be accepted");
        return result.ToString();
    }

    [Theory]
    [InlineData("intro.html", "https://www.example.com/docs/intro.html")]
    [InlineData("./intro.html", "https://www.example.com/docs/intro.html")]
    [InlineData("../pricing", "https://www.example.com/pricing")]
    [InlineData("/contact", "https://www.example.com/contact")]
    [InlineData("?page=2", "https://www.example.com/docs/guide.html?page=2")]
    [InlineData("?t=10:30", "https://www.example.com/docs/guide.html?t=10:30")] // a colon in the query is not a scheme
    [InlineData("  /contact  ", "https://www.example.com/contact")]           // surrounding whitespace is ignored
    public void Relative_links_resolve_against_the_page_they_were_found_on(string href, string expected)
    {
        Normalize(href).ShouldBe(expected);
    }

    [Theory]
    [InlineData("intro.html#section-2", "https://www.example.com/docs/intro.html")]
    [InlineData("https://www.example.com/docs/guide.html#top", "https://www.example.com/docs/guide.html")]
    [InlineData("#top", "https://www.example.com/docs/guide.html")]
    public void The_fragment_is_dropped_so_anchors_on_one_page_are_one_page(string href, string expected)
    {
        Normalize(href).ShouldBe(expected);
    }

    [Theory]
    [InlineData("HTTPS://WWW.Example.COM/Docs/Guide.html", "https://www.example.com/Docs/Guide.html")]
    [InlineData("https://www.example.com:443/a", "https://www.example.com/a")]
    [InlineData("http://www.example.com:80/a", "http://www.example.com/a")]
    [InlineData("http://www.example.com:8080/a", "http://www.example.com:8080/a")]
    public void Scheme_and_host_are_lowercased_default_ports_dropped_path_case_kept(string href, string expected)
    {
        Normalize(href).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://www.example.com/about/", "https://www.example.com/about/")]
    [InlineData("https://www.example.com/about", "https://www.example.com/about")]
    [InlineData("https://www.example.com/", "https://www.example.com/")]
    [InlineData("https://www.example.com", "https://www.example.com/")]
    public void The_path_is_kept_as_written_including_a_trailing_slash(string href, string expected)
    {
        Normalize(href).ShouldBe(expected);
    }

    [Fact]
    public void The_query_string_is_kept_exactly_as_written()
    {
        Normalize("/search?b=2&a=1").ShouldBe("https://www.example.com/search?b=2&a=1");
    }

    [Fact]
    public void Protocol_relative_links_take_the_scheme_of_the_page()
    {
        Normalize("//cdn.example.net/lib.js").ShouldBe("https://cdn.example.net/lib.js");
    }

    [Theory]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+1-555-0100")]
    [InlineData("javascript:void(0)")]
    [InlineData("ftp://files.example.com/x")]
    [InlineData("data:text/plain,hi")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Non_web_links_and_empty_hrefs_are_rejected(string? href)
    {
        UrlNormalizer.TryNormalize(href, Page, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("https://exa mple.com/")]
    [InlineData("http://[::1")]
    public void A_malformed_href_is_rejected_rather_than_thrown(string href)
    {
        UrlNormalizer.TryNormalize(href, Page, out _).ShouldBeFalse();
    }

    [Fact]
    public void Percent_encoding_does_not_split_one_page_into_two_keys()
    {
        Normalize("/a%20b").ShouldBe(Normalize("/a b"));
        Normalize("/caf%C3%A9").ShouldBe(Normalize("/café"));
    }

    [Fact]
    public void Two_spellings_of_one_page_produce_one_key()
    {
        var a = Normalize("HTTPS://www.example.com/About#team");
        var b = Normalize("/About", new Uri("https://www.example.com/"));

        a.ShouldBe(b);
    }

    [Fact]
    public void A_link_too_long_to_store_is_rejected()
    {
        var huge = "/download?token=" + new string('x', 3000);

        UrlNormalizer.TryNormalize(huge, Page, out _).ShouldBeFalse();
    }
}
