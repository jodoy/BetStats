using System.Globalization;
using System.Text;
using BetStats.Application.Ingestion;
using BetStats.Domain.Football;
using BetStats.Domain.Sports;

namespace BetStats.Infrastructure.Ingestion;

// Explicit fictional format. The original metadata parser and its keys are unchanged.
public sealed class FootballResultsCsvParser : IFootballMetadataParser
{
    public const string Version = "results-v1";
    public FootballParseResult Parse(ReadOnlyMemory<byte> bytes, FootballImportScope scope, CancellationToken cancellationToken = default)
    {
        FootballParseResult Fail(string code) => new(0, [], [new(0, code)], ParserVersion: Version);
        if (!scope.IsValid || bytes.Length > 1_048_576) return Fail("invalid_scope_or_size");
        List<string[]> rows;
        try { rows = FootballDataCsvParser.Csv(new UTF8Encoding(false, true).GetString(bytes.Span).TrimStart('\uFEFF'), cancellationToken); }
        catch (Exception e) when (e is DecoderFallbackException or InvalidDataException) { return Fail("malformed_result_csv"); }
        string[] required = ["Div", "Date", "HomeTeam", "AwayTeam", "MatchId", "Status", "ResultBasis", "FTHG", "FTAG", "HTHG", "HTAG", "PublishedAtUtc"];
        if (rows.Count == 0 || rows[0].Length != required.Length || rows[0].Distinct(StringComparer.Ordinal).Count() != required.Length || required.Any(h => !rows[0].Contains(h, StringComparer.Ordinal))) return Fail("invalid_result_headers");
        var records = new List<FootballMatchRecord>(); var issues = new List<ImportIssue>();
        var seen = new Dictionary<string, FootballMatchRecord>(StringComparer.Ordinal); var collisions = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var number = i + 1; var row = rows[i];
            if (row.Length != required.Length) { issues.Add(new(number, "schema_mismatch")); continue; }
            string Get(string key) => row[Array.IndexOf(rows[0], key)].Trim();
            if (!Enum.GetNames<FootballMatchStatus>().Contains(Get("Status"), StringComparer.Ordinal) || !Enum.TryParse<FootballMatchStatus>(Get("Status"), out var status) ||
                !Enum.GetNames<FootballScoreBasis>().Contains(Get("ResultBasis"), StringComparer.Ordinal) || !Enum.TryParse<FootballScoreBasis>(Get("ResultBasis"), out var basis) || Get("MatchId").Length == 0)
            { issues.Add(new(number, "invalid_result_status_or_basis")); continue; }
            int? Goal(string key) => Get(key) is "" or "unknown" or "unavailable" ? null :
                int.TryParse(Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
            DateTime? published = null;
            if (Get("PublishedAtUtc").Length > 0)
            {
                if (!DateTime.TryParseExact(Get("PublishedAtUtc"), "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p)) { issues.Add(new(number, "invalid_result_publication_time")); continue; }
                published = p;
            }
            string Quote(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            var metadata = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\n" + string.Join(',', new[] { Get("Div"), Get("Date"), Get("HomeTeam"), Get("AwayTeam"), status == FootballMatchStatus.Finished ? "D" : "", Get("MatchId") }.Select(Quote)) + "\n";
            var parsed = new FootballDataCsvParser().Parse(Encoding.UTF8.GetBytes(metadata), scope, cancellationToken);
            if (parsed.Records.Count != 1) { issues.AddRange(parsed.Issues.Select(x => x with { Row = number })); continue; }
            var eventStatus = status switch { FootballMatchStatus.Finished => SportingEventStatus.Completed,
                FootballMatchStatus.Live or FootballMatchStatus.HalfTime => SportingEventStatus.InProgress,
                FootballMatchStatus.Postponed => SportingEventStatus.Postponed,
                FootballMatchStatus.Cancelled or FootballMatchStatus.Abandoned => SportingEventStatus.Cancelled, _ => SportingEventStatus.Scheduled };
            var record = parsed.Records[0] with { Row = number, Status = eventStatus,
                Result = new(new(status, basis, new(Goal("FTHG"), Goal("FTAG")), new(Goal("HTHG"), Goal("HTAG"))), published) };
            if (seen.TryGetValue(record.MatchReference, out var previous))
            {
                var equivalent = previous with { Row = number } == record && !collisions.Contains(record.MatchReference);
                if (!equivalent) { records.RemoveAll(r => r.MatchReference == record.MatchReference); collisions.Add(record.MatchReference); issues.Add(new(previous.Row, "result_duplicate_conflict")); }
                issues.Add(new(number, equivalent ? "duplicate_reference" : "result_duplicate_conflict")); continue;
            }
            seen.Add(record.MatchReference, record); records.Add(record);
        }
        return new(rows.Count - 1, records, issues, ParserVersion: Version);
    }
}

public sealed class FootballFixtureParser : IVersionedFootballMetadataParser
{
    public FootballParseResult ParseProfile(ReadOnlyMemory<byte> bytes, FootballImportScope scope, string profile, CancellationToken cancellationToken = default) => profile switch
    {
        HistoricalFootballCsvParser.Version => new HistoricalFootballCsvParser().Parse(bytes, scope, cancellationToken),
        FootballResultsCsvParser.Version => new FootballResultsCsvParser().Parse(bytes, scope, cancellationToken),
        FootballDataCsvParser.Version => new FootballDataCsvParser().Parse(bytes, scope, cancellationToken),
        _ => new(0, [], [new(0, "unsupported_parser_version")], CompletePayload: false, ParserVersion: profile)
    };
    public static string Profile(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span[..Math.Min(bytes.Length, 1024)]).Split('\n')[0].Split(',')
        .Contains("ResultBasis", StringComparer.Ordinal) ? FootballResultsCsvParser.Version : FootballDataCsvParser.Version;
    public FootballParseResult Parse(ReadOnlyMemory<byte> bytes, FootballImportScope scope, CancellationToken cancellationToken = default)
    {
        // Dispatch only an explicit format header, never reinterpret an old metadata fixture.
        return Profile(bytes) == FootballResultsCsvParser.Version
            ? new FootballResultsCsvParser().Parse(bytes, scope, cancellationToken)
            : new FootballDataCsvParser().Parse(bytes, scope, cancellationToken);
    }
}
