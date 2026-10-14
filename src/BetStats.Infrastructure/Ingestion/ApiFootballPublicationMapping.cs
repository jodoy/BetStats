using System.Globalization;
using System.Security.Cryptography;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Football;
using BetStats.Domain.Sports;

namespace BetStats.Infrastructure.Ingestion;

// Mapping commands, not canonical entities or authorization receipts. Deliberately
// unregistered until the transactional publisher can enforce current DB governance.
public sealed record ApiFootballPublicationContext(int Version, Guid SourceId, string Provider,
    string Profile, Guid RawId, string RawHash, FootballImportScope OriginalScope,
    Guid PolicyId, int PolicyVersion, DateTime RetrievedUtc, DateTime RecordedUtc,
    ApiFootballCanary Canary);

public sealed record ApiFootballMappedFixture(int MappingVersion, string EventReference,
    string CompetitionReference, string SeasonReference, string HomeReference, string AwayReference,
    DateTime? KickoffUtc, SportingEventStatus? Status, FootballResultValue? FinalResult,
    string ProviderStatus, IReadOnlyList<string> MetadataExclusions, IReadOnlyList<string> ResultExclusions);

public static class ApiFootballPublicationMapping
{
    public const int Version = 1;
    public static IReadOnlyList<ApiFootballMappedFixture> Map(ReadOnlyMemory<byte> originalRaw,
        ApiFootballPublicationContext context, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (originalRaw.Length is < 1 or > 1048576)
            throw new InvalidDataException("api_football_raw_size_invalid");
        if (context is null || context.Version != 1 || context.SourceId == Guid.Empty ||
            context.Provider != "api-football" || context.Profile != ApiFootballFixtureAdapter.Profile ||
            context.RawId == Guid.Empty || context.PolicyId == Guid.Empty || context.PolicyVersion < 1 ||
            context.Canary is null || !context.Canary.IsValid || context.OriginalScope is null ||
            context.OriginalScope.CompetitionReference != context.Canary.League.ToString(CultureInfo.InvariantCulture) ||
            context.OriginalScope.SeasonReference != context.Canary.Season.ToString(CultureInfo.InvariantCulture) ||
            context.RetrievedUtc.Kind != DateTimeKind.Utc || context.RecordedUtc.Kind != DateTimeKind.Utc ||
            context.RetrievedUtc.Ticks % 10 != 0 || context.RecordedUtc.Ticks % 10 != 0 ||
            context.RecordedUtc < context.RetrievedUtc)
            throw new InvalidDataException("api_football_publication_context_invalid");
        // A caller's declared hash is not proof: compare the exact original bytes.
        if (context.RawHash != Convert.ToHexStringLower(SHA256.HashData(originalRaw.Span)))
            throw new InvalidDataException("api_football_raw_hash_mismatch");
        var fixtures = ApiFootballFixtureReader.Read(originalRaw, context.Canary);
        var result = new List<ApiFootballMappedFixture>(fixtures.Count);
        foreach (var fixture in fixtures)
        {
            token.ThrowIfCancellationRequested();
            SportingEventStatus? status = fixture.Status switch
            {
                "NS" => SportingEventStatus.Scheduled,
                "1H" or "HT" or "2H" or "ET" or "BT" or "P" or "LIVE" => SportingEventStatus.InProgress,
                "FT" or "AET" or "PEN" => SportingEventStatus.Completed,
                "PST" => SportingEventStatus.Postponed,
                "CANC" => SportingEventStatus.Cancelled,
                // Suspended, interrupted, abandoned, awarded and walkover have no
                // exact canonical equivalent. Never infer completion/cancellation.
                _ => null
            };
            var metadata = fixture.Blockers.Where(b => b is "unknown_kickoff_timezone" or
                "missing_or_conflicting_kickoff_precision").ToList();
            if (status is null) metadata.Add("unsupported_canonical_status");
            var resultExclusions = new List<string>();
            FootballResultValue? final = null;
            if (fixture.Status != "FT") resultExclusions.Add("unsupported_final_result_status_or_basis");
            else if (fixture.FullHome is null || fixture.FullAway is null) resultExclusions.Add("missing_final_result");
            else
                final = new(FootballMatchStatus.Finished, FootballScoreBasis.RegulationTime,
                    new(fixture.FullHome, fixture.FullAway), new(fixture.HalfHome, fixture.HalfAway));
            // Missing half-time scores limit period labels, not a supported full-time result.
            if (fixture.Status == "FT" && (fixture.HalfHome is null || fixture.HalfAway is null))
                resultExclusions.Add("missing_half_time_result");
            if (fixture.KickoffUtc is null) resultExclusions.Add("event_time_unavailable");
            result.Add(new(Version, Reference("fixture", fixture.Id),
                "provider:competition:" + context.OriginalScope.CompetitionReference,
                FootballDataCsvParser.SeasonReference(context.OriginalScope.CompetitionReference, context.OriginalScope.SeasonReference),
                Reference("team", fixture.HomeId), Reference("team", fixture.AwayId), fixture.KickoffUtc,
                status, final, fixture.Status, metadata.ToArray(), resultExclusions.ToArray()));
        }
        return result;
    }

    // Every lookup must additionally constrain ProviderIdentity.DataSourceId.
    public static string Reference(string kind, int id)
    {
        if (kind is not ("fixture" or "team") || id <= 0) throw new ArgumentException("Supported positive provider reference required.");
        return "provider:api-football:" + kind + ":" + id.ToString(CultureInfo.InvariantCulture);
    }
}
