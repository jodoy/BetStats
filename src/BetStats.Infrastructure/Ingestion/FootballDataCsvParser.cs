using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetStats.Application.Ingestion;
using BetStats.Domain.Sports;

namespace BetStats.Infrastructure.Ingestion;

public sealed class FootballDataCsvParser : IFootballMetadataParser
{
    public const string Version = "metadata-v1";
    public static string TeamReference(string competition, string name) => Composite("team", competition, name);
    public static string SeasonReference(string competition, string season) => Composite("season", competition, season);
    private static string Composite(string kind, params string[] parts) => "composite:" + kind + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    public FootballParseResult Parse(ReadOnlyMemory<byte> bytes, FootballImportScope scope, CancellationToken cancellationToken = default)
    {
        if (!scope.IsValid || bytes.Length > 1_048_576) return new(0, [], [new(0, "invalid_scope_or_size")]);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes.Span).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return new(0, [], [new(0, "invalid_encoding")]); }
        List<string[]> rows;
        try { rows = Csv(text, cancellationToken); }
        catch (InvalidDataException) { return new(0, [], [new(0, "malformed_csv")]); }
        if (rows.Count == 0) return new(0, [], [new(0, "invalid_headers")]);
        var headers = rows[0];
        string[] required = ["Div", "Date", "HomeTeam", "AwayTeam"];
        string[] allowed = [.. required, "FTR", "Time", "MatchId", "FTHG", "FTAG", "HTHG", "HTAG", "HTR"];
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length || required.Any(h => !headers.Contains(h, StringComparer.Ordinal)) || headers.Any(h => !allowed.Contains(h, StringComparer.Ordinal)))
            return new(rows.Count - 1, [], [new(0, "invalid_headers")]);
        var records = new List<FootballMatchRecord>(); var issues = new List<ImportIssue>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal); var collisions = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var row = rows[index]; var rowNumber = index + 1;
            string Get(string key) { var position = Array.IndexOf(headers, key); return position < 0 ? "" : row[position].Trim(); }
            if (row.Length != headers.Length) { issues.Add(new(rowNumber, "schema_mismatch")); continue; }
            var div = Get("Div"); var dateText = Get("Date"); var home = Get("HomeTeam"); var away = Get("AwayTeam");
            if (div.Length == 0 || home.Length == 0 || away.Length == 0 || home.Length > 200 || away.Length > 200 || home == away) { issues.Add(new(rowNumber, "missing_or_invalid_values")); continue; }
            if (div != scope.CompetitionReference) { issues.Add(new(rowNumber, "unsupported_competition")); continue; }
            // Expand two-digit years explicitly using the supplied season context, not locale pivots.
            if (!DateOnly.TryParseExact(dateText, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                if (dateText.Length != 8 || !int.TryParse(scope.SeasonReference.AsSpan(0, Math.Min(4, scope.SeasonReference.Length)), out var startYear) || startYear < 1900 || startYear > 2100 ||
                    !DateOnly.TryParseExact(dateText[..6] + (startYear / 100).ToString(CultureInfo.InvariantCulture) + dateText[6..], "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                { issues.Add(new(rowNumber, "invalid_date")); continue; }
            }
            var ftr = Get("FTR");
            if (ftr.Length != 0 && ftr is not ("H" or "D" or "A")) { issues.Add(new(rowNumber, "invalid_status")); continue; }
            var time = Get("Time");
            if (time.Length != 0 && !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { issues.Add(new(rowNumber, "invalid_time")); continue; }
            var external = Get("MatchId");
            if (external.Length > 400) { issues.Add(new(rowNumber, "invalid_match_reference")); continue; }
            var reference = external.Length == 0 ? Composite("event", scope.CompetitionReference, scope.SeasonReference, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), home, away) : "provider:" + external;
            var signature = JsonSerializer.Serialize(new[] { date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), home, away, ftr.Length == 0 ? "unknown" : "completed" });
            if (seen.TryGetValue(reference, out var prior))
            {
                var collision = prior != signature || collisions.Contains(reference);
                if (collision)
                {
                    foreach (var previousRecord in records.Where(r => r.MatchReference == reference).ToArray()) issues.Add(new(previousRecord.Row, "identity_collision"));
                    records.RemoveAll(r => r.MatchReference == reference); collisions.Add(reference);
                }
                issues.Add(new(rowNumber, collision ? "identity_collision" : "duplicate_reference")); continue;
            }
            seen.Add(reference, signature);
            records.Add(new(rowNumber, div, scope.SeasonReference, date, TeamReference(div, home), TeamReference(div, away), home, away,
                reference, external.Length == 0, ftr.Length == 0 ? null : SportingEventStatus.Completed));
        }
        return new(rows.Count - 1, records, issues);
    }
    private static List<string[]> Csv(string text, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>(); var cells = new List<string>(); var value = new StringBuilder(); var quoted = false; var closed = false;
        void Cell() { cells.Add(value.ToString()); value.Clear(); closed = false; if (cells.Count > 16) throw new InvalidDataException(); }
        void Row() { Cell(); rows.Add(cells.ToArray()); cells.Clear(); if (rows.Count > 5001) throw new InvalidDataException(); }
        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var c = text[index];
            if (quoted)
            {
                if (c == '"') { if (index + 1 < text.Length && text[index + 1] == '"') { value.Append('"'); index++; } else { quoted = false; closed = true; } }
                else value.Append(c);
            }
            else if (c == '"') { if (value.Length != 0 || closed) throw new InvalidDataException(); quoted = true; }
            else if (c == ',') Cell();
            else if (c is '\r' or '\n') { if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++; Row(); }
            else { if (closed || c == '\0') throw new InvalidDataException(); value.Append(c); }
            if (value.Length > 4096) throw new InvalidDataException();
        }
        if (quoted) throw new InvalidDataException();
        if (value.Length > 0 || cells.Count > 0 || closed) Row();
        return rows;
    }
}
