using Crawler.Domain;

namespace Crawler.Domain.Tests;

public class ResultTests
{
    [Fact]
    public void Fail_carries_its_reason()
    {
        var r = Result.Fail("nope");
        r.IsSuccess.ShouldBeFalse();
        r.Error.ShouldBe("nope");
    }
}
