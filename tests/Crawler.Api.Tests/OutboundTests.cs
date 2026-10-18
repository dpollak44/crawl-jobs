using System.Net;
using System.Net.Http.Json;
using Crawler.Api;
using Crawler.Api.Claims;
using Crawler.Api.Outbound;

namespace Crawler.Api.Tests;

/// <summary>Each test gets its own factory so the scripted payer and the outbound table start empty.</summary>
public class OutboundTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public OutboundTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Transient_payer_errors_are_retried_and_the_call_succeeds()
    {
        _factory.Payer.Enqueue(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var claim = await CreateClaimAsync("OUT-1");

        var response = await _client.PostAsync($"/claims/{claim.Id}/send", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var call = (await response.Content.ReadFromJsonAsync<OutboundCallResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        call.Status.ShouldBe("Succeeded");
        call.Attempts.ShouldBe(1);
        _factory.Payer.Requests.ShouldBe(3);
    }

    [Fact]
    public async Task A_payer_that_stays_down_leaves_a_failed_row_that_a_person_can_retry()
    {
        _factory.Payer.Enqueue(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError);
        var claim = await CreateClaimAsync("OUT-2");

        var send = await _client.PostAsync($"/claims/{claim.Id}/send", content: null, TestContext.Current.CancellationToken);
        var failed = (await send.Content.ReadFromJsonAsync<OutboundCallResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        failed.Status.ShouldBe("Failed");
        failed.LastError.ShouldNotBeNull().ShouldContain("500");
        _factory.Payer.Requests.ShouldBe(4); // 1 attempt + 3 retries

        var queue = (await _client.GetFromJsonAsync<Page<OutboundCallResponse>>("/outbound?status=Failed&pageSize=10", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        queue.Items.Select(c => c.Id).ShouldContain(failed.Id);
        queue.Total.ShouldBe(1);
        queue.PageSize.ShouldBe(10);

        _factory.Payer.Enqueue(HttpStatusCode.OK);
        var retry = await _client.PostAsync($"/outbound/{failed.Id}/retry", content: null, TestContext.Current.CancellationToken);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var retried = (await retry.Content.ReadFromJsonAsync<OutboundCallResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        retried.Status.ShouldBe("Succeeded");
        retried.Attempts.ShouldBe(2);
        retried.LastError.ShouldBeNull();

        var again = await _client.PostAsync($"/outbound/{failed.Id}/retry", content: null, TestContext.Current.CancellationToken);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private async Task<ClaimResponse> CreateClaimAsync(string externalRef)
    {
        var create = await _client.PostAsJsonAsync("/claims", new CreateClaimRequest(externalRef, "Jane Doe", 10m), TestContext.Current.CancellationToken);
        return (await create.Content.ReadFromJsonAsync<ClaimResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }
}
