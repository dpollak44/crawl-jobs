using System.Net;

namespace Crawler.Api.Tests;

public class HealthTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public HealthTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Proves ROUTING ONLY: the health endpoint is mapped and answers. The test host has no broker, so the overall
    /// verdict is Unhealthy here; the real proof that the checks work is the compose health checks gating start-up.
    /// </summary>
    [Fact]
    public async Task Health_endpoint_is_routed_and_answers_this_proves_wiring_only()
    {
        var response = await _client.GetAsync("/health");

        ((int)response.StatusCode).ShouldBeOneOf(200, 503);
    }
}
