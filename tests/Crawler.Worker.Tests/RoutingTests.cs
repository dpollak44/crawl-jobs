using System.Net.Sockets;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Crawling;
using Crawler.Worker.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client.Exceptions;

namespace Crawler.Worker.Tests;

/// <summary>What happens to a message after processing, and which failures count as "our infrastructure is down".</summary>
public class RoutingTests
{
    private static readonly CrawlPageMessage Msg = new(Guid.NewGuid(), Guid.NewGuid(), "https://shop.test/", 1);

    [Fact]
    public void Done_is_acked_and_poison_is_dead_lettered()
    {
        ConsumerRouting.Decide(new ProcessResult(ProcessOutcome.Done), Msg, 3).ShouldBe(RouteAction.Ack);
        ConsumerRouting.Decide(new ProcessResult(ProcessOutcome.Poison, "no such job"), Msg, 3).ShouldBe(RouteAction.DeadLetter);
    }

    [Fact]
    public void A_one_off_failure_spends_an_attempt_and_gives_up_at_the_budget()
    {
        ConsumerRouting.Decide(new ProcessResult(ProcessOutcome.Transient), Msg with { Attempt = 1 }, 3).ShouldBe(RouteAction.RetryNextAttempt);
        ConsumerRouting.Decide(new ProcessResult(ProcessOutcome.Transient), Msg with { Attempt = 2 }, 3).ShouldBe(RouteAction.RetryNextAttempt);
        ConsumerRouting.Decide(new ProcessResult(ProcessOutcome.Transient), Msg with { Attempt = 3 }, 3).ShouldBe(RouteAction.GiveUpAndDeadLetter);
    }

    [Fact]
    public void An_infrastructure_outage_waits_without_spending_an_attempt_however_many_times()
    {
        ConsumerRouting.Decide(new ProcessResult(ProcessOutcome.InfrastructureUnavailable), Msg with { Attempt = 1 }, 3).ShouldBe(RouteAction.RetrySameAttempt);
        ConsumerRouting.Decide(new ProcessResult(ProcessOutcome.InfrastructureUnavailable), Msg with { Attempt = 3 }, 3).ShouldBe(RouteAction.RetrySameAttempt);
        ConsumerRouting.Decide(new ProcessResult(ProcessOutcome.InfrastructureUnavailable), Msg with { Attempt = 99 }, 3).ShouldBe(RouteAction.RetrySameAttempt);
    }

    [Fact]
    public void Connection_failures_to_postgres_or_rabbitmq_count_as_infrastructure_down_even_when_wrapped()
    {
        FailureKind.IsInfrastructureUnavailable(new NpgsqlException("connection refused", new SocketException())).ShouldBeTrue();
        FailureKind.IsInfrastructureUnavailable(new DbUpdateException("save failed", new NpgsqlException("timeout", new TimeoutException()))).ShouldBeTrue();
        FailureKind.IsInfrastructureUnavailable(new BrokerUnreachableException(new SocketException())).ShouldBeTrue();
    }

    [Fact]
    public void A_unique_index_violation_or_a_plain_bug_is_not_an_outage()
    {
        FailureKind.IsInfrastructureUnavailable(new DbUpdateException("duplicate key", new InvalidOperationException("23505"))).ShouldBeFalse();
        FailureKind.IsInfrastructureUnavailable(new DbUpdateConcurrencyException("stale")).ShouldBeFalse();
        FailureKind.IsInfrastructureUnavailable(new NullReferenceException()).ShouldBeFalse();
    }
}
