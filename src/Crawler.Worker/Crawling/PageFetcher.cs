using Crawler.Domain.Crawling;
using Crawler.Infrastructure;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Crawler.Worker.Crawling;

public enum FetchOutcome
{
    Html,         // 2xx with an HTML body: parse it
    NotHtml,      // 2xx but not a page (PDF, image, JSON): record and move on
    HttpError,    // 4xx/5xx after retries, or a redirect loop
    Unreachable,  // DNS, connection, timeout after retries
    OutOfScope,   // a redirect led off the site or to a non-web / private address: nothing was read
}

public sealed record FetchResult(FetchOutcome Outcome, Uri FinalUrl, int? StatusCode, string? Html, string? Error);

/// <summary>
/// The only code that touches the outside internet. Retries with backoff and the per-attempt timeout come
/// from the HttpClient pipeline configured in Program.cs; this class decides what a reply means.
///
/// Redirects are followed here, explicitly, rather than by the HttpClient: the framework refuses an
/// https → http redirect on principle, but for a crawler the same host over http is the same site, and we
/// want to record where the page finally landed. Hops are capped by MaxRedirects. Bodies are read only when
/// the reply says HTML, and never past MaxBodyBytes.
/// </summary>
public sealed class PageFetcher(HttpClient http, IOptions<CrawlOptions> options)
{
    public Task<FetchResult> FetchAsync(Uri url, CancellationToken ct) => FetchAsync(url, _ => true, ct);

    /// <param name="mayFollow">Decides, before anything is read, whether a redirect target may be fetched (same site, public address).</param>
    public async Task<FetchResult> FetchAsync(Uri url, Func<Uri, bool> mayFollow, CancellationToken ct)
    {
        if (!AddressGuard.IsPublicWebAddress(url))
        {
            return new FetchResult(FetchOutcome.OutOfScope, url, null, null, $"Not a public web address ({url.Scheme}://{url.Host}); not requested.");
        }

        var current = url;
        for (var hop = 0; hop <= options.Value.MaxRedirects; hop++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is AddressRefusedException refused)
            {
                return refused.Reason == AddressRefusal.Private
                    ? new FetchResult(FetchOutcome.OutOfScope, current, null, null, refused.Message)
                    : new FetchResult(FetchOutcome.Unreachable, current, null, null, refused.Message);
            }
            catch (HttpRequestException ex)
            {
                return new FetchResult(FetchOutcome.Unreachable, current, (int?)ex.StatusCode, null, $"Could not reach the page: {ex.Message}");
            }
            catch (Exception ex) when (ex is TimeoutRejectedException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // The site did not answer in time, across every retry. That is the site's problem, not ours: record and move on.
                return new FetchResult(FetchOutcome.Unreachable, current, null, null, $"Timed out after {options.Value.FetchTimeoutSeconds} s per attempt, {options.Value.FetchRetries} retries.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        return new FetchResult(FetchOutcome.HttpError, current, status, null, $"HTTP {status} without a Location header.");
                    }

                    Uri next;
                    try
                    {
                        next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    }
                    catch (UriFormatException)
                    {
                        return new FetchResult(FetchOutcome.HttpError, current, status, null, $"HTTP {status} redirect to an unreadable address.");
                    }

                    if (!AddressGuard.IsPublicWebAddress(next))
                    {
                        return new FetchResult(FetchOutcome.OutOfScope, next, status, null, $"Redirected to a non-web or private address ({next.Scheme}://{next.Host}); not followed.");
                    }

                    if (!mayFollow(next))
                    {
                        return new FetchResult(FetchOutcome.OutOfScope, next, status, null, $"Redirected off the site to {next.Host}; not followed.");
                    }

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new FetchResult(FetchOutcome.HttpError, current, status, null, $"HTTP {status} {response.ReasonPhrase}");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    return new FetchResult(FetchOutcome.NotHtml, current, status, null, $"Not an HTML page ({(mediaType.Length == 0 ? "no content type" : mediaType)}).");
                }

                // The per-attempt timeout covered the headers only (ResponseHeadersRead). Give the body its own clock,
                // or a server that drips one byte a minute would hold this worker forever.
                using var bodyClock = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bodyClock.CancelAfter(TimeSpan.FromSeconds(options.Value.FetchTimeoutSeconds));
                try
                {
                    return await ReadHtmlAsync(response, current, status, bodyClock.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new FetchResult(FetchOutcome.Unreachable, current, status, null, $"Timed out reading the page body after {options.Value.FetchTimeoutSeconds} s.");
                }
            }
        }

        return new FetchResult(FetchOutcome.HttpError, current, null, null, $"Gave up after {options.Value.MaxRedirects} redirects.");
    }

    private async Task<FetchResult> ReadHtmlAsync(HttpResponseMessage response, Uri finalUrl, int status, CancellationToken ct)
    {
        var max = options.Value.MaxBodyBytes;
        if (response.Content.Headers.ContentLength is > 0 and var declared && declared > max)
        {
            return new FetchResult(FetchOutcome.NotHtml, finalUrl, status, null, $"Page is {declared / 1024} KB; limit is {max / 1024} KB.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (limited.Length + read > max)
            {
                return new FetchResult(FetchOutcome.NotHtml, finalUrl, status, null, $"Page exceeds the {max / 1024} KB limit; not parsed.");
            }

            limited.Write(buffer, 0, read);
        }

        var bytes = limited.GetBuffer();
        var length = (int)limited.Length;
        var encoding = TryGetEncoding(response.Content.Headers.ContentType?.CharSet) ?? TryGetEncoding(SniffMetaCharset(bytes, length)) ?? System.Text.Encoding.UTF8;
        return new FetchResult(FetchOutcome.Html, finalUrl, status, encoding.GetString(bytes, 0, length), null);
    }

    private static readonly System.Text.RegularExpressions.Regex MetaCharset = new("""charset\s*=\s*["']?\s*([A-Za-z0-9_\-]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>When the header does not say, the page's own &lt;meta charset&gt; in the first 2 KB usually does.</summary>
    private static string? SniffMetaCharset(byte[] bytes, int length)
    {
        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(length, 2048));
        var m = MetaCharset.Match(head);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static System.Text.Encoding? TryGetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return null;
        }

        try
        {
            return System.Text.Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
