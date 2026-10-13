using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BetStats.Application.Providers;

namespace BetStats.Infrastructure.Ingestion;

// Provider DTO, never a canonical event, prediction input, or durable observation receipt.
public sealed record ApiFootballFixture(int Id, int HomeId, int AwayId, DateTime? KickoffUtc,
    string Status, int? FullHome, int? FullAway, int? HalfHome, int? HalfAway, IReadOnlyList<string> Blockers);

public static partial class ApiFootballFixtureReader
{
    public static IReadOnlyList<ApiFootballFixture> Read(ReadOnlyMemory<byte> originalRaw, ApiFootballCanary scope)
    {
        if (!scope.IsValid || originalRaw.Length is < 1 or > 1048576) throw Invalid();
        try
        {
            using var doc = JsonDocument.Parse(originalRaw, new() { MaxDepth = 32 });
            var root = doc.RootElement; Unique(root);
            if (root.GetProperty("get").GetString() != "fixtures" ||
                root.GetProperty("errors") is var errors && !Empty(errors)) throw Invalid();
            var paging = root.GetProperty("paging");
            // Official fixtures endpoint is a bounded single response. No silent truncation.
            if (paging.GetProperty("current").GetInt32() != 1 || paging.GetProperty("total").GetInt32() != 1) throw Invalid();
            var response = root.GetProperty("response");
            if (response.ValueKind != JsonValueKind.Array || response.GetArrayLength() > 500 || root.GetProperty("results").GetInt32() != response.GetArrayLength()) throw Invalid();
            var result = new List<ApiFootballFixture>(); var ids = new HashSet<int>();
            foreach (var item in response.EnumerateArray())
            {
                var fixture = item.GetProperty("fixture"); var id = Positive(fixture.GetProperty("id"));
                if (!ids.Add(id)) throw Invalid(); // Both identical duplicates and conflicts fail closed.
                var league = item.GetProperty("league");
                if (Positive(league.GetProperty("id")) != scope.League || league.GetProperty("season").GetInt32() != scope.Season) throw Invalid();
                var teams = item.GetProperty("teams"); var home = Positive(teams.GetProperty("home").GetProperty("id"));
                var away = Positive(teams.GetProperty("away").GetProperty("id")); if (home == away) throw Invalid();
                var status = fixture.GetProperty("status").GetProperty("short").GetString();
                if (status is not ("TBD" or "NS" or "1H" or "HT" or "2H" or "ET" or "BT" or "P" or "SUSP" or "INT" or "FT" or "AET" or "PEN" or "PST" or "CANC" or "ABD" or "AWD" or "WO" or "LIVE")) throw Invalid();
                var blockers = new List<string>(); DateTime? kickoff = null;
                var date = fixture.GetProperty("date").GetString(); var timestamp = fixture.GetProperty("timestamp");
                if (fixture.GetProperty("timezone").GetString() != "UTC") blockers.Add("unknown_kickoff_timezone");
                else if (date is null || !UtcSeconds().IsMatch(date) || !DateTimeOffset.TryParseExact(date,
                    ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var instant) || timestamp.ValueKind != JsonValueKind.Number ||
                    !timestamp.TryGetInt64(out var unix) || instant.ToUnixTimeSeconds() != unix) blockers.Add("missing_or_conflicting_kickoff_precision");
                else
                {
                    kickoff = instant.UtcDateTime;
                    var day = DateOnly.FromDateTime(kickoff.Value);
                    if (day < scope.From || day > scope.To) throw Invalid();
                }
                if (status != "NS" && status != "FT") blockers.Add("unsupported_prediction_or_result_status");
                var score = item.GetProperty("score");
                var full = score.GetProperty("fulltime"); var half = score.GetProperty("halftime");
                var fh = Goals(full.GetProperty("home")); var fa = Goals(full.GetProperty("away"));
                var hh = Goals(half.GetProperty("home")); var ha = Goals(half.GetProperty("away"));
                var goals = item.GetProperty("goals");
                if (status == "NS" && (fh is not null || fa is not null || hh is not null || ha is not null ||
                    Goals(goals.GetProperty("home")) is not null || Goals(goals.GetProperty("away")) is not null)) throw Invalid();
                if (status == "FT")
                {
                    if (fh is null || fa is null) blockers.Add("missing_final_result");
                    if (hh is null || ha is null) blockers.Add("missing_half_time_result");
                    if (hh > fh || ha > fa) throw Invalid();
                    if (Goals(goals.GetProperty("home")) != fh || Goals(goals.GetProperty("away")) != fa) throw Invalid();
                }
                result.Add(new(id, home, away, kickoff, status!, fh, fa, hh, ha, blockers));
            }
            return result;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    private static int Positive(JsonElement value) { var id = value.GetInt32(); return id > 0 ? id : throw Invalid(); }
    private static int? Goals(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32() is >= 0 and <= 1000 and var goals ? goals : throw Invalid();
    private static bool Empty(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0 ||
        value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any();
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private static InvalidDataException Invalid() => new("api_football_schema_invalid");
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\+00:00|Z)$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcSeconds();
}
