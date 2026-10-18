using Crawler.Domain.Model;
using FluentValidation;

namespace Crawler.Api.Jobs;

public sealed record CreateJobRequest(string Url, int? MaxDepth, int? MaxPages);

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
            .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .WithMessage("The address must be a full http or https URL, for example https://example.com.");
        RuleFor(x => x.MaxDepth).InclusiveBetween(0, maxDepthLimit).When(x => x.MaxDepth.HasValue)
            .WithMessage($"Depth must be between 0 and {maxDepthLimit}.");
        RuleFor(x => x.MaxPages).InclusiveBetween(1, maxPagesLimit).When(x => x.MaxPages.HasValue)
            .WithMessage($"Page limit must be between 1 and {maxPagesLimit}.");
    }
}

public static class StatusText
{
    public static string Of(JobStatus s) => s.ToString();
    public static string Of(PageStatus s) => s.ToString();
}
