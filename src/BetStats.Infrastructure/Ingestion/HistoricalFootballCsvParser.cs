using System.Globalization;
using System.Text;
using BetStats.Application.Ingestion;

namespace BetStats.Infrastructure.Ingestion;

// Versioned, bounded football-data style CSV projection. Original files are retained
// unchanged. Odds and unrelated columns are deliberately outside this profile.
public sealed class HistoricalFootballCsvParser : IFootballMetadataParser
{
    public const string Version = "football-history-v1";
    public FootballParseResult Parse(ReadOnlyMemory<byte> bytes, FootballImportScope scope, CancellationToken cancellationToken = default)
    {
        FootballParseResult Fail(string code) => new(0, [], [new(0, code)], CompletePayload: false, ParserVersion: Version);
        if (!scope.IsValid || bytes.Length is 0 or > 1_048_576) return Fail("invalid_scope_or_size");
        List<string[]> rows;
        try { rows = FootballDataCsvParser.Csv(new UTF8Encoding(false, true).GetString(bytes.Span).TrimStart('\uFEFF'), cancellationToken); }
        catch (Exception e) when (e is DecoderFallbackException or InvalidDataException) { return Fail("malformed_csv"); }
        string[] required = ["Div", "Date", "HomeTeam", "AwayTeam", "FTHG", "FTAG"];
        string[] allowed = [.. required, "HTHG", "HTAG", "FTR", "HTR", "Time", "MatchId", "Status", "ResultBasis", "PublishedAtUtc"];
        if (rows.Count == 0 || rows[0].Distinct(StringComparer.Ordinal).Count() != rows[0].Length ||
            required.Any(h => !rows[0].Contains(h, StringComparer.Ordinal)) || rows[0].Any(h => !allowed.Contains(h, StringComparer.Ordinal))) return Fail("invalid_headers");
        var output = new StringBuilder("Div,Date,HomeTeam,AwayTeam,MatchId,Status,ResultBasis,FTHG,FTAG,HTHG,HTAG,PublishedAtUtc\n");
        var issues = new List<ImportIssue>(); var rowNumbers = new List<int>();
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        for (var i = 1; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var row = rows[i];
            if (row.Length != rows[0].Length) { issues.Add(new(i + 1, "schema_mismatch")); continue; }
            string Get(string key) { var index = Array.IndexOf(rows[0], key); return index < 0 ? "" : row[index].Trim(); }
            // No two-digit year pivot or inferred season. Reviewed season interval is also checked at publication.
            if (!DateOnly.TryParseExact(Get("Date"), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            { issues.Add(new(i + 1, "invalid_date")); continue; }
            if (Get("Time").Length > 0 && !TimeOnly.TryParseExact(Get("Time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            { issues.Add(new(i + 1, "invalid_time")); continue; }
            var metadata = new FootballDataCsvParser().Parse(Encoding.UTF8.GetBytes("Div,Date,HomeTeam,AwayTeam,MatchId\n" +
                string.Join(',', new[] { Get("Div"), Get("Date"), Get("HomeTeam"), Get("AwayTeam"), Get("MatchId") }.Select(Quote)) + "\n"), scope, cancellationToken);
            if (metadata.Records.Count != 1) { issues.AddRange(metadata.Issues.Select(x => x with { Row = i + 1 })); continue; }
            static bool Unknown(string value) => value is "" or "unknown" or "unavailable";
            var status = Get("Status");
            if (status.Length == 0) status = !Unknown(Get("FTHG")) && !Unknown(Get("FTAG")) ? "Finished" : "Scheduled";
            var basis = Get("ResultBasis"); if (basis.Length == 0) basis = status == "Finished" ? "RegulationTime" : "Unknown";
            bool Consistent(string homeKey, string awayKey, string labelKey)
            {
                var label = Get(labelKey);
                if (label.Length == 0) return true;
                if (!int.TryParse(Get(homeKey), NumberStyles.None, CultureInfo.InvariantCulture, out var home) ||
                    !int.TryParse(Get(awayKey), NumberStyles.None, CultureInfo.InvariantCulture, out var away)) return false;
                return label == (home > away ? "H" : home < away ? "A" : "D");
            }
            if (!Consistent("FTHG", "FTAG", "FTR") || !Consistent("HTHG", "HTAG", "HTR")) { issues.Add(new(i + 1, "conflicting_score_label")); continue; }
            var reference = metadata.Records[0].MatchReference;
            // Feed the existing result validator, then restore the original identity reference.
            output.AppendLine(string.Join(',', new[] { Get("Div"), Get("Date"), Get("HomeTeam"), Get("AwayTeam"), reference,
                status, basis, Get("FTHG"), Get("FTAG"), Get("HTHG"), Get("HTAG"), Get("PublishedAtUtc") }.Select(Quote)));
            rowNumbers.Add(i + 1);
        }
        var parsed = new FootballResultsCsvParser().Parse(Encoding.UTF8.GetBytes(output.ToString()), scope, cancellationToken);
        int Original(int row) => row >= 2 && row - 2 < rowNumbers.Count ? rowNumbers[row - 2] : 0;
        var records = parsed.Records.Select(r => r with { Row = Original(r.Row), MatchReference = r.MatchReference["provider:".Length..],
            CompositeMatchReference = r.MatchReference.StartsWith("provider:composite:", StringComparison.Ordinal) }).ToArray();
        issues.AddRange(parsed.Issues.Select(x => x with { Row = Original(x.Row) }));
        // Same fixture under distinct provider IDs is ambiguous even when scores agree.
        var duplicateFixtures = records.GroupBy(r => (r.MatchDate, r.HomeReference, r.AwayReference)).Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
        issues.AddRange(duplicateFixtures.Select(r => new ImportIssue(r.Row, "identity_collision")));
        return new(rows.Count - 1, records.Except(duplicateFixtures).ToArray(), issues, CompletePayload: issues.Count == 0, ParserVersion: Version);
    }
}
