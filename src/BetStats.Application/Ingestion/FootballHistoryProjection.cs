using System.Globalization;
using System.Text;

namespace BetStats.Application.Ingestion;

/// <summary>
/// Result of projecting a football-data style export onto the football-history-v1 column set.
/// </summary>
public sealed record FootballHistoryProjectionResult(
    byte[] Content,
    int InputRows,
    int ProjectedRows,
    int OtherCompetitionRows,
    IReadOnlyList<ImportIssue> Issues);

/// <summary>
/// Projects a football-data style CSV export (any extra columns, two- or four-digit years) onto the
/// bounded football-history-v1 profile. Only the selected competition is kept. The original file is
/// never modified; the caller stores the projection separately.
/// </summary>
public static class FootballHistoryProjection
{
    public const int MaximumInputBytes = 4 * 1024 * 1024;
    public const int MaximumProjectedRows = 5000;

    private static readonly string[] Required = ["Div", "Date", "HomeTeam", "AwayTeam", "FTHG", "FTAG"];
    private static readonly string[] Optional = ["Time", "HTHG", "HTAG", "FTR", "HTR"];

    public static FootballHistoryProjectionResult Project(ReadOnlySpan<byte> bytes, FootballImportScope scope)
    {
        if (!scope.IsValid)
        {
            throw new ArgumentException("Explicit competition and season are required.");
        }

        if (bytes.Length is 0 or > MaximumInputBytes)
        {
            throw new InvalidDataException("input_empty_or_too_large");
        }

        var startYear = SeasonStartYear(scope.SeasonReference);
        var lines = ReadLines(Decode(bytes));
        if (lines.Count == 0)
        {
            throw new InvalidDataException("invalid_headers");
        }

        var header = lines[0].Select(h => h.Trim()).ToArray();
        if (Required.Any(column => Array.IndexOf(header, column) < 0))
        {
            throw new InvalidDataException("invalid_headers");
        }

        var columns = Required.Take(2)
            .Concat(Optional.Take(1))
            .Concat(Required.Skip(2))
            .Concat(Optional.Skip(1))
            .Where(column => Array.IndexOf(header, column) >= 0)
            .ToArray();
        var indexes = columns.Select(column => Array.IndexOf(header, column)).ToArray();

        var output = new StringBuilder();
        output.Append(string.Join(',', columns)).Append('\n');
        var issues = new List<ImportIssue>();
        var inputRows = 0;
        var projectedRows = 0;
        var otherRows = 0;

        for (var i = 1; i < lines.Count; i++)
        {
            var row = lines[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            inputRows++;
            var rowNumber = i + 1;
            if (row.Length <= indexes.Max())
            {
                issues.Add(new(rowNumber, "schema_mismatch"));
                continue;
            }

            var division = row[Array.IndexOf(header, "Div")].Trim();
            if (!string.Equals(division, scope.CompetitionReference, StringComparison.Ordinal))
            {
                otherRows++;
                continue;
            }

            var values = indexes.Select(index => row[index].Trim()).ToArray();
            var dateIndex = Array.IndexOf(columns, "Date");
            var date = NormalizeDate(values[dateIndex], startYear);
            if (date is null)
            {
                issues.Add(new(rowNumber, "invalid_date"));
                continue;
            }

            values[dateIndex] = date;
            output.Append(string.Join(',', values.Select(Quote))).Append('\n');
            projectedRows++;
        }

        if (projectedRows > MaximumProjectedRows)
        {
            throw new InvalidDataException("too_many_rows");
        }

        var content = new UTF8Encoding(false).GetBytes(output.ToString());
        return new(content, inputRows, projectedRows, otherRows, issues);
    }

    /// <summary>
    /// Expands dd/MM/yy using the explicit season start year; dd/MM/yyyy is kept unchanged.
    /// A two-digit year lower than the start year belongs to the next century boundary.
    /// </summary>
    public static string? NormalizeDate(string value, int seasonStartYear)
    {
        const string Format = "dd/MM/yyyy";
        if (DateOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
        {
            return full.ToString(Format, CultureInfo.InvariantCulture);
        }

        if (value.Length != 8 || value[2] != '/' || value[5] != '/')
        {
            return null;
        }

        if (!int.TryParse(value.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var shortYear))
        {
            return null;
        }

        var century = seasonStartYear / 100 * 100;
        var year = century + shortYear;
        if (shortYear < seasonStartYear % 100)
        {
            year += 100;
        }

        var expanded = value[..6] + year.ToString("0000", CultureInfo.InvariantCulture);
        if (!DateOnly.TryParseExact(expanded, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        return parsed.ToString(Format, CultureInfo.InvariantCulture);
    }

    public static int SeasonStartYear(string seasonReference)
    {
        var digits = seasonReference.Length >= 4 ? seasonReference[..4] : seasonReference;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year is < 1900 or > 2100)
        {
            throw new ArgumentException("Season reference must start with a four-digit year, for example 2023-2024.");
        }

        return year;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("invalid_encoding");
        }
    }

    private static List<string[]> ReadLines(string text)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (quoted)
            {
                if (c != '"')
                {
                    value.Append(c);
                }
                else if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    cells.Add(value.ToString());
                    value.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    cells.Add(value.ToString());
                    value.Clear();
                    rows.Add([.. cells]);
                    cells.Clear();
                    break;
                default:
                    value.Append(c);
                    break;
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("malformed_csv");
        }

        if (value.Length > 0 || cells.Count > 0)
        {
            cells.Add(value.ToString());
            rows.Add([.. cells]);
        }

        return rows;
    }

    private static string Quote(string value)
    {
        var needsQuotes = value.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
