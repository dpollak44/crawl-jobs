using System.Net;
using System.Net.Sockets;
using Crawler.Domain.Crawling;

namespace Crawler.Domain.Tests;

public class AddressGuardTests
{
    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("https://example.com./", true)]         // trailing dot is the same name
    [InlineData("http://sub.example.org/x", true)]
    [InlineData("https://8.8.8.8/", true)]
    [InlineData("http://[2606:4700::1111]/", true)]      // a global IPv6 address
    [InlineData("http://localhost/admin", false)]
    [InlineData("http://LOCALHOST./", false)]
    [InlineData("http://app.localhost/", false)]
    [InlineData("http://127.0.0.1:8080/", false)]
    [InlineData("http://10.0.0.5/", false)]
    [InlineData("http://172.16.4.4/", false)]
    [InlineData("http://172.32.0.1/", true)]             // just outside 172.16/12
    [InlineData("http://192.168.1.1/", false)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("http://100.64.0.1/", false)]            // carrier-grade NAT
    [InlineData("http://[::1]/", false)]
    [InlineData("http://[fe80::1]/", false)]
    [InlineData("http://[fd00::1]/", false)]
    [InlineData("http://intranet.internal/", false)]
    [InlineData("http://printer.local/", false)]
    [InlineData("http://nodots/", false)]
    [InlineData("ftp://example.com/", false)]
    public void Only_public_web_addresses_may_be_fetched(string url, bool allowed)
    {
        AddressGuard.IsPublicWebAddress(new Uri(url)).ShouldBe(allowed);
    }

    [Fact]
    public void Host_names_are_normalized_before_comparison()
    {
        AddressGuard.NormalizeHost("  Example.COM. ").ShouldBe("example.com");
    }

    [Fact]
    public async Task At_connection_time_a_name_that_resolves_to_a_private_address_is_refused()
    {
        Task<IPAddress[]> Resolve(string h, CancellationToken _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3"), IPAddress.Parse("192.168.0.9") });

        var allowed = await AddressGuard.ResolveAllowedAsync("looks-public.example.com", Resolve, CancellationToken.None);

        allowed.ShouldBeEmpty();
    }

    [Fact]
    public async Task At_connection_time_only_the_public_addresses_of_a_mixed_name_are_dialled()
    {
        Task<IPAddress[]> Resolve(string h, CancellationToken _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3"), IPAddress.Parse("93.184.216.34") });

        var allowed = await AddressGuard.ResolveAllowedAsync("example.com.", Resolve, CancellationToken.None);

        allowed.Select(a => a.ToString()).ShouldBe(["93.184.216.34"]);
    }

    [Fact]
    public async Task At_connection_time_a_name_that_does_not_resolve_is_refused_not_thrown()
    {
        Task<IPAddress[]> Resolve(string h, CancellationToken _) => throw new SocketException((int)SocketError.HostNotFound);

        (await AddressGuard.ResolveAllowedAsync("nope.example.com", Resolve, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task At_connection_time_a_literal_address_is_not_resolved_but_still_vetted()
    {
        var calls = 0;
        Task<IPAddress[]> Resolve(string h, CancellationToken _) { calls++; return Task.FromResult(Array.Empty<IPAddress>()); }

        (await AddressGuard.ResolveAllowedAsync("169.254.169.254", Resolve, CancellationToken.None)).ShouldBeEmpty();
        (await AddressGuard.ResolveAllowedAsync("8.8.8.8", Resolve, CancellationToken.None)).Count.ShouldBe(1);
        calls.ShouldBe(0);
    }
}
