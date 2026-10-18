using Crawler.Domain;

namespace Crawler.Domain.Tests;

public class OutboundCallTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_failed_call_can_be_retried_and_keeps_its_attempt_count()
    {
        var call = OutboundCall.Create("payer", "{}", Now);
        call.MarkFailed("payer answered 503", Now);

        call.PrepareRetry().IsSuccess.ShouldBeTrue();
        call.Status.ShouldBe(OutboundStatus.Pending);
        call.MarkSucceeded(Now.AddMinutes(1));

        call.Attempts.ShouldBe(2);
        call.LastError.ShouldBeNull();
        call.Status.ShouldBe(OutboundStatus.Succeeded);
    }

    [Fact]
    public void A_succeeded_call_cannot_be_retried()
    {
        var call = OutboundCall.Create("payer", "{}", Now);
        call.MarkSucceeded(Now);

        var result = call.PrepareRetry();

        result.IsSuccess.ShouldBeFalse();
        call.Status.ShouldBe(OutboundStatus.Succeeded);
    }
}
