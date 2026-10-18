using System.Net;
using System.Net.Http.Json;
using Crawler.Api.Claims;

namespace Crawler.Api.Tests;

public class ClaimEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public ClaimEndpointsTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_reports_the_database_is_reachable()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Create_then_get_round_trips_a_claim()
    {
        var create = await _client.PostAsJsonAsync("/claims", new CreateClaimRequest("RT-1", "Jane Doe", 99.95m), TestContext.Current.CancellationToken);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await create.Content.ReadFromJsonAsync<ClaimResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        create.Headers.Location.ShouldNotBeNull().ToString().ShouldBe($"/claims/{created.Id}");

        var get = await _client.GetAsync($"/claims/{created.Id}", TestContext.Current.CancellationToken);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fetched = await get.Content.ReadFromJsonAsync<ClaimResponse>(TestContext.Current.CancellationToken);
        fetched.ShouldBe(created);
    }

    [Fact]
    public async Task Create_with_bad_input_returns_400_naming_each_field_in_plain_words()
    {
        var response = await _client.PostAsJsonAsync("/claims", new CreateClaimRequest("", "", 0), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("ExternalRef");
        body.ShouldContain("PatientName");
        body.ShouldContain("Amount must be more than zero.");
    }

    [Fact]
    public async Task Create_with_a_reference_already_held_returns_409()
    {
        await _client.PostAsJsonAsync("/claims", new CreateClaimRequest("DUP-1", "Jane Doe", 1m), TestContext.Current.CancellationToken);

        var again = await _client.PostAsJsonAsync("/claims", new CreateClaimRequest("DUP-1", "Someone Else", 2m), TestContext.Current.CancellationToken);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Submit_twice_returns_409_with_a_readable_reason()
    {
        var create = await _client.PostAsJsonAsync("/claims", new CreateClaimRequest("SUB-1", "Jane Doe", 10m), TestContext.Current.CancellationToken);
        var created = (await create.Content.ReadFromJsonAsync<ClaimResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();

        var first = await _client.PostAsync($"/claims/{created.Id}/submit", content: null, TestContext.Current.CancellationToken);
        var second = await _client.PostAsync($"/claims/{created.Id}/submit", content: null, TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Only a Draft claim can be submitted");
    }

    [Fact]
    public async Task Every_state_change_leaves_an_audit_row_with_who_when_and_what()
    {
        var create = await _client.PostAsJsonAsync("/claims", new CreateClaimRequest("AUD-1", "Jane Doe", 10m), TestContext.Current.CancellationToken);
        var created = (await create.Content.ReadFromJsonAsync<ClaimResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();

        using var submit = new HttpRequestMessage(HttpMethod.Post, $"/claims/{created.Id}/submit");
        submit.Headers.Add("X-Actor", "jdoe");
        (await _client.SendAsync(submit, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var history = (await _client.GetFromJsonAsync<List<ClaimHistoryEntry>>($"/claims/{created.Id}/history", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        history.Count.ShouldBe(1);
        history[0].Action.ShouldBe("Submit");
        history[0].From.ShouldBe("Draft");
        history[0].To.ShouldBe("Submitted");
        history[0].Actor.ShouldBe("jdoe");
    }

    [Fact]
    public async Task Unknown_claim_returns_404()
    {
        var response = await _client.GetAsync($"/claims/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
