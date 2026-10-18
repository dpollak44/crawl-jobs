using Crawler.Domain.Crawling;
using Crawler.Domain.Model;
using FluentValidation;

namespace Crawler.Api.Jobs;

public sealed record CreateJobRequest(string Url, int? MaxDepth, int? MaxPages)
{
    /// <summary>People type "example.com". Assume https when no scheme is given; redirects handle sites that only speak http.</summary>
    public string UrlWithScheme => Url is null ? string.Empty : (Url.Trim().Contains("://", StringComparison.Ordinal) ? Url.Trim() : "https://" + Url.Trim());
}

public sealed record CreateJobResponse(Guid JobId);

public sealed record JobProgress(int Discovered, int Fetched, int Failed, int Skipped, int Queued);

public sealed record JobSummary(
    Guid JobId,
    string Url,
    string Status,
    int MaxDepth,
    int MaxPages,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    JobProgress Progress);

public sealed record TreeNode(
    Guid PageId,
    string Url,
    int Depth,
    string Status,
    decimal DomainLinkRatio,
    int InternalLinks,
    int OutgoingLinks,
    string? Error,
    List<TreeNode> Children);

public sealed class CreateJobRequestValidator : AbstractValidator<CreateJobRequest>
{
    public CreateJobRequestValidator(int maxDepthLimit, int maxPagesLimit)
    {
        RuleFor(x => x.Url)
            .NotEmpty().WithMessage("Enter the address of the page to start from.")
            .MaximumLength(UrlNormalizer.MaxLength).WithMessage("That address is too long to crawl (limit 2,048 characters).");
        RuleFor(x => x.UrlWithScheme)
            .Must(u => TryParseWebAddress(u, out _))
            .When(x => !string.IsNullOrWhiteSpace(x.Url))
            .OverridePropertyName("Url")
            .WithMessage("That does not look like a website address. Try something like example.com or https://example.com/page.");
        RuleFor(x => x.UrlWithScheme)
            .Must(u => !TryParseWebAddress(u, out var uri) || AddressGuard.IsPublicWebAddress(uri)) // only once the shape rule above passed
            .When(x => !string.IsNullOrWhiteSpace(x.Url))
            .OverridePropertyName("Url")
            .WithMessage("Only public websites can be crawled; private or internal addresses are refused.");
        RuleFor(x => x.MaxDepth).InclusiveBetween(0, maxDepthLimit).When(x => x.MaxDepth.HasValue)
            .WithMessage($"Depth must be between 0 and {maxDepthLimit}.");
        RuleFor(x => x.MaxPages).InclusiveBetween(1, maxPagesLimit).When(x => x.MaxPages.HasValue)
            .WithMessage($"Page limit must be between 1 and {maxPagesLimit}.");
    }

    /// <summary>Parses an http(s) address whose host is a dotted name or an IP literal. Bare words ("localhost", "intranet") are not website addresses.</summary>
    private static bool TryParseWebAddress(string? text, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (!parsed.Host.Contains('.') && AddressGuard.TryParseIpLiteral(parsed.Host) is null)
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
