using Crawler.Domain;

namespace Crawler.Domain.Tests;

public class ClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_as_draft_at_version_1_and_trims_text()
    {
        var claim = Claim.Create(" C-1 ", "  Jane Doe ", 125.50m, Now);

        claim.Status.ShouldBe(ClaimStatus.Draft);
        claim.Version.ShouldBe(1);
        claim.ExternalRef.ShouldBe("C-1");
        claim.PatientName.ShouldBe("Jane Doe");
        claim.CreatedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_a_non_positive_amount(decimal amount)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Claim.Create("C-1", "Jane Doe", amount, Now));
    }

    [Fact]
    public void Create_requires_an_external_reference()
    {
        Should.Throw<ArgumentException>(() => Claim.Create(" ", "Jane Doe", 1m, Now));
    }

    [Fact]
    public void Submit_moves_draft_to_submitted_and_bumps_the_version()
    {
        var claim = Claim.Create("C-1", "Jane Doe", 10m, Now);

        var result = claim.Submit();

        result.IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Submitted);
        claim.Version.ShouldBe(2);
    }

    [Fact]
    public void Submit_twice_fails_and_leaves_the_claim_alone()
    {
        var claim = Claim.Create("C-1", "Jane Doe", 10m, Now);
        claim.Submit();

        var result = claim.Submit();

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("Submitted");
        claim.Status.ShouldBe(ClaimStatus.Submitted);
        claim.Version.ShouldBe(2);
    }

    [Fact]
    public void Adjudicate_requires_a_submitted_claim()
    {
        var claim = Claim.Create("C-1", "Jane Doe", 10m, Now);

        claim.Adjudicate(approved: true).IsSuccess.ShouldBeFalse();

        claim.Submit();
        claim.Adjudicate(approved: false).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Denied);
    }
}
