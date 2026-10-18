using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Crawler.Api.Jobs;

/// <summary>
/// Open streams by job id, and the relay from "job X changed" to those streams. A notification carries no
/// data: on one, if the job has subscribers, the summary is read from the database ONCE and pushed to each.
/// No subscribers → dropped. Each subscriber keeps only the latest summary (a bounded channel that drops the
/// oldest), so a slow browser never builds a backlog. The database stays the only source of truth; a
/// notification delivered twice costs one harmless extra read.
/// </summary>
public sealed class ProgressRelay(Func<Guid, CancellationToken, Task<JobSummary?>> load)
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<JobSummary>>> _subscribers = new();

    public int SubscriberCount(Guid jobId) => _subscribers.TryGetValue(jobId, out var subs) ? subs.Count : 0;

    public (ChannelReader<JobSummary> Updates, IDisposable Subscription) Subscribe(Guid jobId)
    {
        var channel = Channel.CreateBounded<JobSummary>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var key = Guid.NewGuid();
        _subscribers.GetOrAdd(jobId, _ => new ConcurrentDictionary<Guid, Channel<JobSummary>>())[key] = channel;
        return (channel.Reader, new Unsubscribe(this, jobId, key));
    }

    /// <summary>Returns true when a summary was read and pushed (i.e. someone was listening).</summary>
    public async Task<bool> NotifyAsync(Guid jobId, CancellationToken ct)
    {
        if (!_subscribers.TryGetValue(jobId, out var subs) || subs.IsEmpty)
        {
            return false;
        }

        var summary = await load(jobId, ct);
        if (summary is null)
        {
            return false;
        }

        foreach (var channel in subs.Values)
        {
            channel.Writer.TryWrite(summary);
        }

        return true;
    }

    private sealed class Unsubscribe(ProgressRelay relay, Guid jobId, Guid key) : IDisposable
    {
        public void Dispose()
        {
            if (relay._subscribers.TryGetValue(jobId, out var subs))
            {
                subs.TryRemove(key, out _);
                if (subs.IsEmpty)
                {
                    relay._subscribers.TryRemove(jobId, out _);
                }
            }
        }
    }
}
