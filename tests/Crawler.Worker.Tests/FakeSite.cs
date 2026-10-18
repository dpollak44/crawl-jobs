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

    /// <summary>A real 3xx reply: the fetcher must follow it (or refuse it) itself.</summary>
    /// <summary>A reply with exact bytes and a media type that names NO charset, so the page's own declaration must be used.</summary>
    public FakeSite Bytes(string url, byte[] body, string mediaType = "text/html")
    {
        _replies[url] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return response;
        };
        return this;
    }

    public FakeSite Redirect(string from, string to, HttpStatusCode status = HttpStatusCode.PermanentRedirect)
    {
        _replies[from] = () =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(to);
            return response;
        };
        return this;
    }

    /// <summary>Answers <paramref name="failures"/> times with <paramref name="status"/>, then serves the fixture. Exercises the HTTP retry policy.</summary>
    public FakeSite Flaky(string url, int failures, HttpStatusCode status, string fixtureFile)
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFile));
        var remaining = failures;
        _replies[url] = () => remaining-- > 0
            ? new HttpResponseMessage(status) { Content = new StringContent("try again later") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };
        return this;
    }

    /// <summary>An HTML reply with no Content-Length header, so the body cap can only be enforced while reading.</summary>
    public FakeSite HtmlWithoutLength(string url, string html)
    {
        _replies[url] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(System.Text.Encoding.UTF8.GetBytes(html)) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return response;
        };
        return this;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private readonly Dictionary<string, TimeSpan> _hangs = new(StringComparer.Ordinal);

    /// <summary>A server that takes this long to answer (then answers 200). Used to exercise the timeout policy.</summary>
    public FakeSite Hang(string url, TimeSpan delay)
    {
        _hangs[url] = delay;
        return this;
    }

    private readonly HashSet<string> _drips = new(StringComparer.Ordinal);

    /// <summary>Headers arrive at once, then the body never completes. Exercises the body-read timeout.</summary>
    public FakeSite Drip(string url)
    {
        _drips.Add(url);
        return this;
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var key = request.RequestUri!.ToString();
        if (_hangs.TryGetValue(key, out var delay))
        {
            await Task.Delay(delay, cancellationToken); // the per-attempt timeout cancels this
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", System.Text.Encoding.UTF8, "text/html"), RequestMessage = request };
        }

        if (_drips.Contains(key))
        {
            var dripping = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverEndingStream()), RequestMessage = request };
            dripping.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return dripping;
        }

        var response = _replies.TryGetValue(key, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
        response.RequestMessage ??= request;
        return response;
    }
}
