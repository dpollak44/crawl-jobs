namespace Crawler.Domain;

/// <summary>
/// A domain operation either succeeds or fails with a reason. No exceptions for expected failures.
/// </summary>
public sealed record Result(bool IsSuccess, string? Error)
{
    public static Result Ok() => new(true, null);
    public static Result Fail(string error) => new(false, error);
}
