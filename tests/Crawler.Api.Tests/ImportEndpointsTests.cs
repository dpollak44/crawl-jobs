using System.Net;
using System.Net.Http.Json;
using System.Text;
using Crawler.Api.FileImports;

namespace Crawler.Api.Tests;

public class ImportEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public ImportEndpointsTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private const string File =
        "external_ref,patient_name,amount\n" +
        "IMP-1,Jane Doe,100.00\n" +
        "IMP-2,John Roe,250.50\n" +
        "IMP-3,,50.00\n" +
        "IMP-4,Ann Poe,abc\n" +
        "IMP-2,John Roe,250.50\n" +
        "IMP-5,Sam Low,5\n";

    [Fact]
    public async Task An_import_ties_out_and_keeps_every_rejected_line_with_its_reason()
    {
        var summary = await ImportAsync("claims.csv", File);

        summary.RowsRead.ShouldBe(6);
        summary.RowsRead.ShouldBe(summary.Accepted + summary.Rejected + summary.Duplicates);
        summary.Accepted.ShouldBe(3);
        summary.Rejected.ShouldBe(2);
        summary.Duplicates.ShouldBe(1);
        summary.Rejections.Select(r => r.LineNumber).ShouldBe([4, 5]);
        summary.Rejections[0].Reason.ShouldContain("patient_name is empty");
        summary.Rejections[1].RawLine.ShouldBe("IMP-4,Ann Poe,abc");

        var stored = (await _client.GetFromJsonAsync<ImportSummaryResponse>($"/imports/{summary.ImportId}", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (stored with { Rejections = [] }).ShouldBe(summary with { Rejections = [] }); // records compare lists by reference
        stored.Rejections.ShouldBe(summary.Rejections);
    }

    [Fact]
    public async Task Importing_the_same_file_again_creates_nothing_new()
    {
        var first = await ImportAsync("again.csv", "external_ref,patient_name,amount\nAG-1,Jane Doe,1\nAG-2,John Roe,2\n");
        var second = await ImportAsync("again.csv", "external_ref,patient_name,amount\nAG-1,Jane Doe,1\nAG-2,John Roe,2\n");

        first.Accepted.ShouldBe(2);
        second.Accepted.ShouldBe(0);
        second.Duplicates.ShouldBe(2);
        second.RowsRead.ShouldBe(second.Accepted + second.Rejected + second.Duplicates);
    }

    [Fact]
    public async Task An_empty_upload_is_a_400()
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent([]), "file", "empty.csv" } };

        var response = await _client.PostAsync("/imports/claims", form, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<ImportSummaryResponse> ImportAsync(string fileName, string content)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName } };
        var response = await _client.PostAsync("/imports/claims", form, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<ImportSummaryResponse>(TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }
}
