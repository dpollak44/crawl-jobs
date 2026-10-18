using Crawler.Domain;
using FluentValidation;

namespace Crawler.Api.Claims;

public sealed record CreateClaimRequest(string ExternalRef, string PatientName, decimal Amount);

public sealed record ClaimResponse(Guid Id, string ExternalRef, string PatientName, decimal Amount, string Status, DateTimeOffset CreatedAt, int Version)
{
    public static ClaimResponse From(Claim c) => new(c.Id, c.ExternalRef, c.PatientName, c.Amount, c.Status.ToString(), c.CreatedAt, c.Version);
}

public sealed record ClaimHistoryEntry(string Action, string? From, string? To, string Actor, DateTimeOffset At);

public sealed class CreateClaimRequestValidator : AbstractValidator<CreateClaimRequest>
{
    public CreateClaimRequestValidator()
    {
        RuleFor(x => x.ExternalRef).NotEmpty().MaximumLength(100);
        RuleFor(x => x.PatientName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("Amount must be more than zero.");
    }
}
