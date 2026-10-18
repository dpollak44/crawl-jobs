namespace Crawler.Api.Outbound;

/// <summary>
/// Typed HttpClient for the example external system. Resilience (retry with backoff, per-attempt timeout,
/// circuit breaker) is attached in Program.cs with AddStandardResilienceHandler, so this class stays plain.
/// </summary>
public sealed class PayerClient(HttpClient http)
{
    public async Task<HttpResponseMessage> PostAsync(string payload, CancellationToken ct)
    {
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        return await http.PostAsync(new Uri("claims", UriKind.Relative), content, ct);
    }
}
