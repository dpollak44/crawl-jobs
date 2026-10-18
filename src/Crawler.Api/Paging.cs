namespace Crawler.Api;

/// <summary>Every list endpoint pages. The cap lives in configuration ("Paging:MaxPageSize"), not in code.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int Total);

public static class Paging
{
    public static (int Skip, int Take, int PageNumber) Resolve(int? page, int? pageSize, IConfiguration config)
    {
        var max = config.GetValue("Paging:MaxPageSize", 200);
        var size = Math.Clamp(pageSize ?? config.GetValue("Paging:DefaultPageSize", 50), 1, max);
        var number = Math.Max(page ?? 1, 1);
        return ((number - 1) * size, size, number);
    }
}
