using System.Net;
using System.Net.Http.Json;
using Crawler.Api.Claims;
using Crawler.Api.Data;
using Crawler.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Api.Tests;

public class ConcurrencyAndErrorTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ConcurrencyAndErrorTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Two_writers_on_the_same_claim_the_second_loses_instead_of_overwriting()
    {
        var create = await _client.PostAsJsonAsync("/claims", new CreateClaimRequest("RACE-1", "Jane Doe", 10m), TestContext.Current.CancellationToken);
        var created = (await create.Content.ReadFromJsonAsync<ClaimResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();

        // Writer A loads the claim at version 1 and holds it.
        using var scopeA = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        var claimA = await dbA.Claims.SingleAsync(c => c.Id == created.Id, TestContext.Current.CancellationToken);

        // Writer B (over HTTP) submits first and wins.
        var submit = await _client.PostAsync($"/claims/{created.Id}/submit", content: null, TestContext.Current.CancellationToken);
        submit.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Writer A's stale save is refused rather than silently reverting B's change.
        claimA.Submit().IsSuccess.ShouldBeTrue();
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => dbA.SaveChangesAsync(TestContext.Current.CancellationToken));

        var final = await _client.GetFromJsonAsync<ClaimResponse>($"/claims/{created.Id}", TestContext.Current.CancellationToken);
        final.ShouldNotBeNull().Version.ShouldBe(2);
    }

    [Fact]
    public async Task A_concurrency_conflict_becomes_a_409_the_user_can_act_on()
    {
        var (status, body) = await RunHandlerAsync(new DbUpdateConcurrencyException("stale"));

        status.ShouldBe(StatusCodes.Status409Conflict);
        body.ShouldContain("changed by someone else");
        body.ShouldContain("Reload");
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_500_with_a_reference_and_no_stack_trace()
    {
        var (status, body) = await RunHandlerAsync(new InvalidOperationException("database exploded at Foo.Bar()"));

        status.ShouldBe(StatusCodes.Status500InternalServerError);
        body.ShouldContain("Something went wrong on our side");
        body.ShouldContain("reference");
        body.ShouldNotContain("exploded");
        body.ShouldNotContain("Foo.Bar");
    }

    private static async Task<(int Status, string Body)> RunHandlerAsync(Exception exception)
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        (await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken)).ShouldBeTrue();

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, body);
    }
}
