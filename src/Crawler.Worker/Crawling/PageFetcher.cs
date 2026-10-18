using System.Net;
using Crawler.Infrastructure;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Crawling;

public enum FetchOutcome
{
    Html,        // 2xx with an HTML body: parse it
    NotHtml,     // 2xx but not a page (PDF, image, JSON): record and move on
    HttpError,   // 4xx/5xx after retries
    Unreachable, // DNS, connection, timeout after retries
}

public sealed record FetchResult(FetchOutcome Outcome, Uri FinalUrl, int? StatusCode, string? Html, string? Error);

/// <summary>
/// The only code that touches the outside internet. Retries with backoff, per-attempt timeout and the
/// redirect cap come from the HttpClient pipeline configured in Program.cs; this class decides what the
/// reply means. Bodies are read only when the reply says HTML, and never past MaxBodyBytes.
/// </summary>
public sealed class PageFetcher(HttpClient http, IOptions<CrawlOptions> options)
{
    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult(FetchOutcome.Unreachable, url, (int?)ex.StatusCode, null, $"Could not reach the page: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult(FetchOutcome.Unreachable, url, null, null, $"Timed out after {options.Value.FetchTimeoutSeconds} s (including retries).");
        }

        using (response)
        {
            var finalUrl = response.RequestMessage?.RequestUri ?? url;
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult(FetchOutcome.HttpError, finalUrl, status, null, $"HTTP {status} {response.ReasonPhrase}");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult(FetchOutcome.NotHtml, finalUrl, status, null, $"Not an HTML page ({(mediaType.Length == 0 ? "no content type" : mediaType)}).");
            }

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

            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = TryGetEncoding(charset) ?? System.Text.Encoding.UTF8;
            return new FetchResult(FetchOutcome.Html, finalUrl, status, encoding.GetString(limited.GetBuffer(), 0, (int)limited.Length), null);
        }
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
