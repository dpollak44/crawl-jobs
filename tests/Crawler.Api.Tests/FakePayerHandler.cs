using System.Collections.Concurrent;
using System.Net;

namespace Crawler.Api.Tests;

/// <summary>Scripted stand-in for the external system. Queue the status codes you want; the default is 200.</summary>
public sealed class FakePayerHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<HttpStatusCode> _script = new();
    private int _requests;

    public int Requests => _requests;

    public void Enqueue(params HttpStatusCode[] codes)
    {
        foreach (var code in codes)
        {
            _script.Enqueue(code);
        }
    }

    public void Reset()
    {
        _script.Clear();
        Interlocked.Exchange(ref _requests, 0);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        var code = _script.TryDequeue(out var next) ? next : HttpStatusCode.OK;
        return Task.FromResult(new HttpResponseMessage(code) { RequestMessage = request });
    }
}
