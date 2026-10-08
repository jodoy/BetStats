namespace BetStats.Application.Datasets;

public sealed record FeatureDefinition(string Name, int Version, string DataType, string Units, int? LookbackDays,
    string MissingSemantics, string RequiredEvidence, string Sport, string TemporalBehavior);
public sealed record FeatureValue(string Name, int Version, int? Value, string? MissingReason, IReadOnlyList<Guid> EvidenceIds);
public sealed record FeatureVector(int SchemaVersion, Guid TargetEventId, DateTime PredictionCutoffUtc, IReadOnlyList<FeatureValue> Values);
public static class FootballMetadataFeatures
{
    public static IReadOnlyList<FeatureDefinition> Catalog { get; } = new[] { "away", "home" }.SelectMany(side => new[] {
        new FeatureDefinition(side + "_days_since_last_observed_completed_match", 1, "nullable-int32", "calendar-days", null, "unavailable", "date,status,identity,RAW,quality,policy", "football", "strictly prior UTC calendar day; partial observed history"),
        new FeatureDefinition(side + "_observed_completed_matches_last_30d", 1, "nullable-int32", "observed-matches", 30, "unavailable", "date,status,identity,RAW,quality,policy", "football", "strictly prior UTC calendar day; lower bound, not total activity")
    }).ToArray();

    public static FeatureVector Compute(DatasetEvidenceReference target, DateTime cutoff, IReadOnlyList<DatasetEvidenceReference> history)
    {
        var day = DateOnly.FromDateTime(cutoff);
        var values = new List<FeatureValue>();
        foreach (var definition in Catalog)
        {
            var participant = definition.Name.StartsWith("home", StringComparison.Ordinal) ? target.HomeId : target.AwayId;
            var relevant = history.Where(e => e.EventId != target.EventId && e.EventDate < target.EventDate && e.EventDate < day &&
                e.DateAvailableUtc <= cutoff && e.DateRecordedUtc <= cutoff && e.RawRecordedUtc <= cutoff &&
                (e.HomeId == participant || e.AwayId == participant) &&
                (definition.LookbackDays is null || e.EventDate.DayNumber >= day.DayNumber - definition.LookbackDays)).ToArray();
            var completed = relevant.Where(e => e.Status == "Completed" && e.StatusObservationId is not null).GroupBy(e => e.EventId).Select(g => g.OrderBy(e => e.DateObservationId).First()).OrderBy(e => e.EventDate).ThenBy(e => e.EventId).ToArray();
            string? missing = relevant.Any(e => e.Status is null) ? "historical_status_missing" : completed.Length == 0 ? "observed_history_missing" : null;
            var used = definition.LookbackDays is null ? completed.TakeLast(1).ToArray() : completed;
            int? result = missing is not null ? null : definition.LookbackDays is null ? target.EventDate.DayNumber - completed[^1].EventDate.DayNumber : completed.Length;
            values.Add(new(definition.Name, definition.Version, result, missing, used.SelectMany(e => new[] { e.DateObservationId, e.StatusObservationId!.Value })
                .Concat(relevant.Where(e => e.Status is null).Select(e => e.DateObservationId)).Distinct().Order().ToArray()));
        }
        return new(1, target.EventId, cutoff, values);
    }
}
