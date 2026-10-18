using Crawler.Api.Jobs;

namespace Crawler.Api.Tests;

public class ProgressRelayTests
{
    private static CancellationToken Deadline => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token; // a regression fails, never hangs

    private static JobSummary Summary(Guid id, string status) =>
        new(id, "https://x.test/", status, 2, 200, DateTimeOffset.UtcNow, null, null, null, new JobProgress(1, 0, 0, 0, 1));

    [Fact]
    public async Task A_notification_with_a_subscriber_reads_the_summary_once_and_pushes_it()
    {
        var jobId = Guid.NewGuid();
        var loads = 0;
        var relay = new ProgressRelay((id, _) => { loads++; return Task.FromResult<JobSummary?>(Summary(id, "Running")); });
        var (updates, subscription) = relay.Subscribe(jobId);
        var (updates2, subscription2) = relay.Subscribe(jobId);

        (await relay.NotifyAsync(jobId, CancellationToken.None)).ShouldBeTrue();

        loads.ShouldBe(1); // one database read per notification, however many streams are open
        (await updates.ReadAsync(Deadline)).Status.ShouldBe("Running");
        (await updates2.ReadAsync(Deadline)).Status.ShouldBe("Running");
        subscription.Dispose();
        subscription2.Dispose();
        relay.SubscriberCount(jobId).ShouldBe(0);
    }

    [Fact]
    public async Task A_notification_with_no_subscriber_is_dropped_without_touching_the_database()
    {
        var loads = 0;
        var relay = new ProgressRelay((id, _) => { loads++; return Task.FromResult<JobSummary?>(Summary(id, "Running")); });

        (await relay.NotifyAsync(Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();

        loads.ShouldBe(0);
    }

    [Fact]
    public async Task A_slow_subscriber_keeps_only_the_latest_summary()
    {
        var jobId = Guid.NewGuid();
        var n = 0;
        var relay = new ProgressRelay((id, _) => Task.FromResult<JobSummary?>(Summary(id, $"v{++n}")));
        var (updates, _) = relay.Subscribe(jobId);

        await relay.NotifyAsync(jobId, CancellationToken.None);
        await relay.NotifyAsync(jobId, CancellationToken.None);
        await relay.NotifyAsync(jobId, CancellationToken.None);

        (await updates.ReadAsync(Deadline)).Status.ShouldBe("v3");
        updates.TryRead(out _).ShouldBeFalse();
    }
}
