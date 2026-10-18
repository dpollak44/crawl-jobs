using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Crawling;

namespace Crawler.Worker.Messaging;

public enum RouteAction
{
    Ack,
    RetrySameAttempt,  // infrastructure outage: wait and try again without spending an attempt
    RetryNextAttempt,  // a one-off failure: spend an attempt
    GiveUpAndDeadLetter,
    DeadLetter,
}

/// <summary>The one place that decides what happens to a message after processing. Pure, so it is tested directly.</summary>
public static class ConsumerRouting
{
    public static RouteAction Decide(ProcessResult result, CrawlPageMessage message, int maxAttempts) => result.Outcome switch
    {
        ProcessOutcome.Done => RouteAction.Ack,
        ProcessOutcome.InfrastructureUnavailable => RouteAction.RetrySameAttempt,
        ProcessOutcome.Transient when message.Attempt < maxAttempts => RouteAction.RetryNextAttempt,
        ProcessOutcome.Transient => RouteAction.GiveUpAndDeadLetter,
        _ => RouteAction.DeadLetter,
    };
}
