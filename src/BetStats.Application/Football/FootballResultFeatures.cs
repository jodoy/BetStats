using BetStats.Application.Datasets;

namespace BetStats.Application.Football;

// Exact fractions avoid binary floating-point and culture-sensitive rounding.
public sealed record ResultFeatureValue(string Name, long? Numerator, int? Denominator, string? MissingReason);
public sealed record FootballResultFeatureVector(int SchemaVersion, Guid TargetEventId, DateTime PredictionCutoffUtc,
    string CoverageSemantics, IReadOnlyList<Guid> ResultObservationIds, IReadOnlyList<ResultFeatureValue> Values);
public static class FootballResultFeatures
{
    public static FootballResultFeatureVector Compute(DatasetEvidenceReference target, DateTime cutoff, IReadOnlyList<FootballResultEvidence> input)
    {
        var day = DateOnly.FromDateTime(cutoff);
        var usable = input.Where(e => e.Eligible && FootballResultRules.LabelEligible(e.Observation.Value) &&
            e.Observation.EventId != target.EventId && e.Observation.EventDate < day && e.Observation.EventDate < target.EventDate &&
            e.Observation.EventDate >= day.AddDays(-30) && e.Observation.AvailableAtUtc <= cutoff && e.Observation.RecordedAtUtc <= cutoff)
            .GroupBy(e => e.Observation.EventId).Select(g => g.OrderBy(e => e.Observation.Id).First()).OrderBy(e => e.Observation.EventId).ToArray();
        var values = new List<ResultFeatureValue>(); var ids = new List<Guid>();
        foreach (var side in new[] { ("home", target.HomeId), ("away", target.AwayId) })
        {
            var matches = usable.Where(e => e.Observation.HomeId == side.Item2 || e.Observation.AwayId == side.Item2).ToArray();
            ids.AddRange(matches.Select(e => e.Observation.Id));
            int n = matches.Length, wins = 0, draws = 0, losses = 0, both = 0, over = 0; long scored = 0, conceded = 0;
            var homeGames = 0; var awayGames = 0; long homePoints = 0, awayPoints = 0;
            foreach (var e in matches)
            {
                var r = e.Observation; var home = r.HomeId == side.Item2;
                var gf = (home ? r.Value.FullTime.Home : r.Value.FullTime.Away)!.Value;
                var ga = (home ? r.Value.FullTime.Away : r.Value.FullTime.Home)!.Value;
                scored += gf; conceded += ga;
                var points = gf > ga ? 3 : gf == ga ? 1 : 0;
                if (gf > ga) wins++; else if (gf == ga) draws++; else losses++;
                if (gf > 0 && ga > 0) both++; if ((long)gf + ga > 2) over++;
                if (home) { homeGames++; homePoints += points; } else { awayGames++; awayPoints += points; }
            }
            void Add(string name, long quantity, int denominator = 1) => values.Add(new(side.Item1 + "_observed_" + name + "_last_30d",
                n == 0 || denominator == 0 ? null : quantity, n == 0 || denominator == 0 ? null : denominator,
                n == 0 ? "eligible_result_history_missing" : denominator == 0 ? "venue_history_missing" : null));
            Add("matches", n); Add("wins", wins); Add("draws", draws); Add("losses", losses);
            Add("goals_scored", scored); Add("goals_conceded", conceded); Add("goal_difference", scored - conceded);
            Add("points_per_match", 3L * wins + draws, n); Add("over_2_5_frequency", over, n); Add("btts_frequency", both, n);
            Add("home_points_per_match", homePoints, homeGames); Add("away_points_per_match", awayPoints, awayGames);
        }
        return new(3, target.EventId, cutoff, "partial-observed-results; metadata coverage does not certify result completeness", ids.Distinct().Order().ToArray(), values);
    }
}

public sealed record FootballResultDatasetRequest(DatasetBuildRequest Metadata, DateTime LabelAsOfUtc);
public sealed record FootballResultDatasetRow(DatasetRow Metadata, FootballResultReport FeatureEvidence,
    FootballResultReport LabelEvidence, FootballResultFeatureVector Features, IReadOnlyList<FootballOutcomeLabels> Labels, string FeatureHash);
public sealed record FootballResultManifest(int ManifestVersion, int FeatureSchemaVersion, int SerializerVersion,
    DatasetManifest MetadataManifest, string MetadataManifestHash, DateTime LabelAsOfUtc, IReadOnlyList<FootballResultDatasetRow> Rows);
public sealed record FootballResultSnapshot(Guid Id, string Hash, FootballResultManifest Manifest);
public sealed record FootballResultVerification(bool Integrity, bool FeaturesReproducible, bool CurrentlyAuthorized);
public interface IFootballResultDatasets
{
    Task<FootballResultSnapshot> BuildAsync(FootballResultDatasetRequest request, CancellationToken token = default);
    Task<FootballResultSnapshot> InspectAsync(Guid id, CancellationToken token = default);
    Task<FootballResultVerification> VerifyAsync(Guid id, CancellationToken token = default);
}
