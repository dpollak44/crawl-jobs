using System.Net;

namespace Crawler.Api.Tests;

public class HealthTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public HealthTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_endpoint_answers()
    {
        // In tests there is no broker, so the overall answer is Unhealthy; what matters is that the endpoint exists and reports per-check.
        var response = await _client.GetAsync("/health");

        ((int)response.StatusCode).ShouldBeOneOf(200, 503);
    }
}
