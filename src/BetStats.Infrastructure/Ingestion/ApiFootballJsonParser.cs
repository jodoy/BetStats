using System.Globalization;
using System.Text.Json;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Football;
using BetStats.Domain.Sports;

namespace BetStats.Infrastructure.Ingestion;

// Read projection for existing validation/dataset/coverage ports. Publication is
// exclusively performed by ProviderPublication, never by the CSV publisher.
public sealed class ApiFootballJsonParser : IFootballMetadataParser
{
    public FootballParseResult Parse(ReadOnlyMemory<byte> bytes, FootballImportScope scope, CancellationToken cancellationToken = default)
    {
        FootballParseResult Fail() => new(0, [], [new(0, "api_football_schema_invalid")], false, ApiFootballFixtureAdapter.Profile);
        if (bytes.Length is < 1 or > 1048576 || !scope.IsValid ||
            !int.TryParse(scope.CompetitionReference, NumberStyles.None, CultureInfo.InvariantCulture, out var league) ||
            !int.TryParse(scope.SeasonReference, NumberStyles.None, CultureInfo.InvariantCulture, out var season)) return Fail();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fixtures = ReadFixtures(bytes, scope);
            var records = new List<FootballMatchRecord>(); var issues = new List<ImportIssue>();
            foreach (var f in fixtures)
            {
                cancellationToken.ThrowIfCancellationRequested(); var row = records.Count + issues.Count + 2;
                SportingEventStatus? status = f.Status switch {
                    "NS" => SportingEventStatus.Scheduled, "FT" or "AET" or "PEN" => SportingEventStatus.Completed,
                    "1H" or "HT" or "2H" or "ET" or "BT" or "P" or "LIVE" => SportingEventStatus.InProgress,
                    "PST" => SportingEventStatus.Postponed, "CANC" => SportingEventStatus.Cancelled, _ => null };
                if (f.KickoffUtc is null || status is null) { issues.Add(new(row, "provider_metadata_unavailable")); continue; }
                var home = ApiFootballPublicationMapping.Reference("team", f.HomeId); var away = ApiFootballPublicationMapping.Reference("team", f.AwayId);
                var final = f.Status == "FT" && f.FullHome is not null && f.FullAway is not null
                    ? new FootballResultInput(new(FootballMatchStatus.Finished, FootballScoreBasis.RegulationTime, new(f.FullHome, f.FullAway), new(f.HalfHome, f.HalfAway)), null) : null;
                records.Add(new(row, scope.CompetitionReference, scope.SeasonReference, DateOnly.FromDateTime(f.KickoffUtc.Value),
                    home, away, home, away, ApiFootballPublicationMapping.Reference("fixture", f.Id), false, status, final));
            }
            return new(fixtures.Count, records, issues, issues.Count == 0, ApiFootballFixtureAdapter.Profile);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        { return Fail(); }
    }
    internal static IReadOnlyList<ApiFootballFixture> ReadFixtures(ReadOnlyMemory<byte> bytes, FootballImportScope scope)
    {
        if (bytes.Length is < 1 or > 1048576) throw new InvalidDataException("api_football_raw_size_invalid");
        var league = int.Parse(scope.CompetitionReference, CultureInfo.InvariantCulture); var season = int.Parse(scope.SeasonReference, CultureInfo.InvariantCulture);
        using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        var dates = doc.RootElement.GetProperty("response").EnumerateArray().Select(r =>
            DateTimeOffset.Parse(r.GetProperty("fixture").GetProperty("date").GetString()!, CultureInfo.InvariantCulture).UtcDateTime).ToArray();
        var from = dates.Length == 0 ? new DateOnly(season, 1, 1) : DateOnly.FromDateTime(dates.Min());
        var to = dates.Length == 0 ? from : DateOnly.FromDateTime(dates.Max());
        return ApiFootballFixtureReader.Read(bytes, new(1, league, season, from, to));
    }
}
