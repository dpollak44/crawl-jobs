using System.Net;

namespace Crawler.Api.Tests;

/// <summary>
/// The health endpoint with its dependency checks replaced by stand-ins with a known answer, so the test proves
/// the mapping (all healthy → 200 "Healthy"; one dependency down → 503 "Unhealthy") and never depends on a
/// developer's local broker. Real connectivity is proven by the compose health checks gating start-up.
/// </summary>
public class HealthTests
{
    [Fact]
    public async Task All_dependencies_healthy_is_200_Healthy()
    {
        using var factory = new ApiFactory { DependenciesHealthy = true };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }

    [Fact]
    public async Task A_dependency_down_is_503_Unhealthy()
    {
        using var factory = new ApiFactory { DependenciesHealthy = false };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldBe("Unhealthy");
    }
}
