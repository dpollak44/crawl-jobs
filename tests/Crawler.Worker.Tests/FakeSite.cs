using System.Net;
using System.Net.Http.Headers;

namespace Crawler.Worker.Tests;

/// <summary>
/// A pretend internet: a map from URL to a scripted reply. HTML replies come from the Fixtures folder.
/// Anything not mapped is a 404. Lets the worker's whole page step run with no network.
/// </summary>
public sealed class FakeSite : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _replies = new(StringComparer.Ordinal);

    public int Requests { get; private set; }

    public FakeSite Html(string url, string fixtureFile)
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFile));
        _replies[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };
        return this;
    }

    public FakeSite Reply(string url, HttpStatusCode status, string? mediaType = null, string body = "")
    {
        _replies[url] = () =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (mediaType is not null)
            {
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            }

            return response;
        };
        return this;
    }

    /// <summary>Simulates an already-followed redirect: the client asked for `from` and ended at `to` with that page's content.</summary>
    public FakeSite Redirect(string from, string to, string fixtureFile)
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFile));
        _replies[from] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, to),
        };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var key = request.RequestUri!.ToString();
        var response = _replies.TryGetValue(key, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}
