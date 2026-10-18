using Crawler.Domain;

namespace Crawler.Domain.Tests;

public class ClaimRowParserTests
{
    private static readonly string[] GoodFile =
    [
        "external_ref,patient_name,amount",
        "C-1,Jane Doe,100.00",
        "C-2,John Roe,250.50",
    ];

    [Fact]
    public void A_clean_file_is_fully_accepted()
    {
        var parsed = ClaimRowParser.Parse(GoodFile);

        parsed.Accepted.Count.ShouldBe(2);
        parsed.Rejected.ShouldBeEmpty();
        parsed.Accepted[1].ShouldBe(new ClaimRow(3, "C-2", "John Roe", 250.50m));
    }

    [Fact]
    public void Every_row_read_is_either_accepted_or_rejected_with_a_reason()
    {
        string[] file =
        [
            "external_ref,patient_name,amount",
            "C-1,Jane Doe,100.00",
            "C-2,,50.00",
            "C-3,John Roe,abc",
            "C-4,John Roe,-5",
            "C-5,John Roe",
            ",John Roe,5",
            "",
            "C-6,Ann Poe,1",
        ];

        var parsed = ClaimRowParser.Parse(file);

        parsed.RowsRead.ShouldBe(parsed.Accepted.Count + parsed.Rejected.Count);
        parsed.Accepted.Select(r => r.ExternalRef).ShouldBe(["C-1", "C-6"]);
        parsed.Rejected.Select(r => r.LineNumber).ShouldBe([3, 4, 5, 6, 7]);
        parsed.Rejected.ShouldAllBe(r => r.Reason.Length > 0 && r.RawLine.Length > 0);
        parsed.Rejected[1].Reason.ShouldContain("not a number");
        parsed.Rejected[2].Reason.ShouldContain("positive");
        parsed.Rejected[3].Reason.ShouldContain("columns");
    }

    [Fact]
    public void A_wrong_header_is_rejected_as_line_1()
    {
        var parsed = ClaimRowParser.Parse(["id,name,amt", "C-1,Jane Doe,1"]);

        parsed.Rejected.Count.ShouldBe(1);
        parsed.Rejected[0].LineNumber.ShouldBe(1);
        parsed.Rejected[0].Reason.ShouldContain("Header");
        parsed.Accepted.Count.ShouldBe(1);
    }
}
