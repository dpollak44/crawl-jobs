namespace Crawler.Domain.Crawling;

public enum AddressRefusal
{
    NotFound, // the name does not resolve
    Private,  // every address it resolves to is private or internal
}

/// <summary>Thrown from the HTTP connect step when a host must not be dialled. Never a reason to retry.</summary>
public sealed class AddressRefusedException(string host, AddressRefusal reason)
    : Exception(reason == AddressRefusal.NotFound
        ? $"The address {host} could not be found."
        : $"{host} resolves only to private or internal addresses; refusing to connect.")
{
    public string Host { get; } = host;
    public AddressRefusal Reason { get; } = reason;
}
