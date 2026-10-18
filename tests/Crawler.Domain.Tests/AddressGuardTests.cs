using Crawler.Domain.Crawling;

namespace Crawler.Domain.Tests;

public class AddressGuardTests
{
    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("http://sub.example.org/x", true)]
    [InlineData("https://8.8.8.8/", true)]
    [InlineData("http://localhost/admin", false)]
    [InlineData("http://app.localhost/", false)]
    [InlineData("http://127.0.0.1:8080/", false)]
    [InlineData("http://10.0.0.5/", false)]
    [InlineData("http://172.16.4.4/", false)]
    [InlineData("http://192.168.1.1/", false)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("http://[::1]/", false)]
    [InlineData("http://intranet.internal/", false)]
    [InlineData("http://nodots/", false)]
    [InlineData("ftp://example.com/", false)]
    public void Only_public_web_addresses_may_be_fetched(string url, bool allowed)
    {
        AddressGuard.IsPublicWebAddress(new Uri(url)).ShouldBe(allowed);
    }
}
