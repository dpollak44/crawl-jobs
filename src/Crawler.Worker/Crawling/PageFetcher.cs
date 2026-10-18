using Crawler.Infrastructure;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Crawler.Worker.Crawling;

public enum FetchOutcome
{
    Html,        // 2xx with an HTML body: parse it
    NotHtml,     // 2xx but not a page (PDF, image, JSON): record and move on
    HttpError,   // 4xx/5xx after retries, or a redirect loop
    Unreachable, // DNS, connection, timeout after retries
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
    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken ct)
    {
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

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
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

                return await ReadHtmlAsync(response, current, status, ct);
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

        var encoding = TryGetEncoding(response.Content.Headers.ContentType?.CharSet) ?? System.Text.Encoding.UTF8;
        return new FetchResult(FetchOutcome.Html, finalUrl, status, encoding.GetString(limited.GetBuffer(), 0, (int)limited.Length), null);
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
