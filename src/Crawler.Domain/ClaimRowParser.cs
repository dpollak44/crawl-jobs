using System.Globalization;

namespace Crawler.Domain;

/// <summary>One accepted line of an import file, ready to become a Claim.</summary>
public sealed record ClaimRow(int LineNumber, string ExternalRef, string PatientName, decimal Amount);

/// <summary>One rejected line, kept with its reason. Nothing is silently dropped.</summary>
public sealed record RejectedRow(int LineNumber, string RawLine, string Reason);

public sealed record ParsedFile(IReadOnlyList<ClaimRow> Accepted, IReadOnlyList<RejectedRow> Rejected)
{
    public int RowsRead => Accepted.Count + Rejected.Count;
}

/// <summary>
/// Parses the example CSV shape: external_ref,patient_name,amount (header row required).
/// Pure: no IO, so the tie-out rule "rows read == accepted + rejected" is unit-tested here.
/// Commas inside quoted fields are not supported; say so in the README if the brief's data needs them.
/// </summary>
public static class ClaimRowParser
{
    public static readonly IReadOnlyList<string> ExpectedHeader = ["external_ref", "patient_name", "amount"];

    public static ParsedFile Parse(IEnumerable<string> lines)
    {
        var accepted = new List<ClaimRow>();
        var rejected = new List<RejectedRow>();
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            if (lineNumber == 1)
            {
                if (!HeaderMatches(raw))
                {
                    rejected.Add(new RejectedRow(lineNumber, raw, $"Header must be: {string.Join(",", ExpectedHeader)}"));
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                continue; // blank trailing lines are not data
            }

            var fields = raw.Split(',');
            if (fields.Length != ExpectedHeader.Count)
            {
                rejected.Add(new RejectedRow(lineNumber, raw, $"Expected {ExpectedHeader.Count} columns, found {fields.Length}."));
                continue;
            }

            var externalRef = fields[0].Trim();
            var patientName = fields[1].Trim();
            if (externalRef.Length == 0)
            {
                rejected.Add(new RejectedRow(lineNumber, raw, "external_ref is empty."));
                continue;
            }

            if (patientName.Length == 0)
            {
                rejected.Add(new RejectedRow(lineNumber, raw, "patient_name is empty."));
                continue;
            }

            if (!decimal.TryParse(fields[2].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                rejected.Add(new RejectedRow(lineNumber, raw, $"amount '{fields[2].Trim()}' is not a number."));
                continue;
            }

            if (amount <= 0)
            {
                rejected.Add(new RejectedRow(lineNumber, raw, $"amount must be positive, found {amount}."));
                continue;
            }

            accepted.Add(new ClaimRow(lineNumber, externalRef, patientName, amount));
        }

        return new ParsedFile(accepted, rejected);
    }

    private static bool HeaderMatches(string raw)
    {
        var cells = raw.Split(',').Select(c => c.Trim().ToLowerInvariant()).ToArray();
        return cells.SequenceEqual(ExpectedHeader);
    }
}
