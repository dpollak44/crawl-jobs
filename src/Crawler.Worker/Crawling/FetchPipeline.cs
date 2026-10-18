using System.Net;
using Crawler.Infrastructure;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Crawler.Worker.Crawling;

/// <summary>
/// The resilience policy for fetching other people's pages, built in one place so the worker and the tests
/// use the same one. Outermost to innermost: a total timeout for the whole fetch, retries with backoff and
/// jitter on transient replies (5xx, 408, 429, network errors, per-attempt timeouts), a per-attempt timeout.
///
/// Deliberately no circuit breaker: one would be shared by every site, so a single dead site would stop
/// every job. A per-host breaker is the right next step.
/// </summary>
public static class FetchPipeline
{
    public static ResiliencePipeline<HttpResponseMessage> Build(CrawlOptions o)
    {
        var attempt = TimeSpan.FromSeconds(o.FetchTimeoutSeconds);
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddTimeout(attempt * (o.FetchRetries + 1) + TimeSpan.FromSeconds(5));

        if (o.FetchRetries > 0) // Polly rejects a retry strategy with zero attempts; "no retries" means no strategy
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = o.FetchRetries,
                Delay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => r.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests),
            });
        }

        return builder.AddTimeout(attempt).Build();
    }
}
