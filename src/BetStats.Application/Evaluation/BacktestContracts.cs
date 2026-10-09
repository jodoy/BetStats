using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Coverage;
using BetStats.Domain.Quality;

namespace BetStats.Application.Evaluation;

// The predictor input deliberately cannot carry labels, provider DTOs or evaluation evidence.
public sealed record PredictionInput(int Version, Guid EventId, Guid HomeId, Guid AwayId, DateTime CutoffUtc,
    string FeatureHash, FootballResultFeatureVector Features, IReadOnlyList<Guid> EvidenceIds,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Models.ModelHistory? History = null);
public sealed record PredictionValue(IReadOnlyList<decimal> Probabilities, decimal? ExpectedCount);
public interface IHistoricalPredictionProvider
{
    string Name { get; }
    int Version { get; }
    PredictionValue Predict(PredictionInput input, EvaluationTarget target);
}
public sealed record HistoricalPrediction(int Version, Guid EventId, DateTime PredictionCutoffUtc, string Predictor,
    int PredictorVersion, Guid FeatureDatasetId, string FeatureDatasetHash, string FeatureHash, string InputHash,
    EvaluationTarget Target, PredictionValue Value, IReadOnlyList<Guid> EvidenceIds,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Models.PredictionModelProvenance? Model = null);
public sealed record BacktestDefinition(int Version, Guid DatasetId, string ExpectedDatasetHash, string Predictor,
    int PredictorVersion, DateTime EvaluationCutoffUtc, IReadOnlyList<EvaluationDefinition> Evaluations,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Models.FootballModelDefinition? Model = null)
{
    public void Validate()
    {
        Model?.Validate();
        if (!(Version == 1 && Model is null && Predictor == "synthetic-constant" || Version == 2 && Model is not null && Predictor == Model.Predictor) || DatasetId == Guid.Empty || !BacktestRules.Hash(ExpectedDatasetHash) || PredictorVersion != 1 ||
            !BacktestRules.Utc(EvaluationCutoffUtc) || Evaluations is null || Evaluations.Count is < 1 or > 6 ||
            Evaluations.Select(e => e.Target).Distinct().Count() != Evaluations.Count) throw new ArgumentException("Explicit bounded backtest v1 definition required.");
        foreach (var e in Evaluations)
        {
            e.Validate();
            if (e.Version is < 3 or > 10000 || e.ReconstructionUtc > EvaluationCutoffUtc) throw new ArgumentException("Backtests require evaluation contract v3 semantics and bounded R/version.");
        }
    }
}
public sealed record BacktestRequest(Guid OperationId, BacktestDefinition Definition, string OperatorId, string Reason, bool Approved);
public sealed record BacktestSample(Guid EventId, EvaluationTarget Target, HistoricalPrediction Prediction,
    Guid? ResultObservationId, int? ResultVersion, bool Eligible, IReadOnlyList<string> Reasons, int? ClassLabel, int? CountLabel);
public sealed record CalibrationBin(int Index, int Count, decimal? MeanProbability, decimal? ObservedFrequency);
public sealed record ClassCalibration(string Class, IReadOnlyList<CalibrationBin> Bins);
public sealed record MetricValue(string Name, int Version, int Denominator, decimal? Value, bool PositiveInfinity,
    bool MinimumSamplesMet, IReadOnlyList<CalibrationBin> Calibration);
public sealed record TargetEvaluation(EvaluationTarget Target, int Requested, int Eligible, int Excluded,
    IReadOnlyList<MetricValue> Metrics, IReadOnlyDictionary<string, int> ExclusionReasons, IReadOnlyList<ClassCalibration> Calibration);
public sealed record BacktestReport(int Version, int MetricVersion, string NumericSemantics, IReadOnlyList<TargetEvaluation> Targets);
public sealed record BacktestAuthorization(Guid SourceId, BetStats.Domain.Governance.DataPurpose Purpose, Guid PolicyId, int PolicyVersion, IReadOnlyList<Guid> AuditIds);
public sealed record BacktestManifest(int Version, int SerializerVersion, BacktestDefinition Definition, string Fingerprint,
    string ExecutionKind, IReadOnlyList<HistoricalPrediction> Predictions, IReadOnlyList<BacktestSample> Samples,
    FootballResultManifest EvaluationEvidence, BacktestReport Report,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BacktestAuthorization>? Authorizations = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Models.WalkForwardReport? WalkForward = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Models.ModelForecast>? ModelForecasts = null);
public sealed record BacktestSnapshot(Guid Id, string Hash, DateTime RecordedAtUtc, BacktestManifest Manifest);
public sealed record BacktestVerification(bool Integrity, bool Reproducible, bool CurrentlyAuthorized, bool? RawAvailable, bool? RawHashVerified);
public interface IHistoricalBacktests
{
    Task<BacktestManifest> PlanAsync(BacktestDefinition definition, CancellationToken token = default);
    Task<ResultOperationResult> RunAsync(BacktestRequest request, CancellationToken token = default);
    Task<ResultOperationResult> RecoverAsync(ResultRecoveryRequest request, CancellationToken token = default);
    Task<ResultOperationResult> OperationAsync(Guid id, CancellationToken token = default);
    Task<BacktestSnapshot> InspectAsync(Guid id, CancellationToken token = default);
    Task<BacktestVerification> VerifyAsync(Guid id, bool deep = false, CancellationToken token = default);
}
public static class BacktestRules
{
    public static bool Utc(DateTime t) => t.Kind == DateTimeKind.Utc && t.Ticks % 10 == 0;
    public static bool Hash(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Count(EvaluationTarget t) => t is EvaluationTarget.TotalGoals or EvaluationTarget.FirstHalfTotalGoals;
    public static IReadOnlyList<string> Classes(EvaluationTarget t) => t == EvaluationTarget.MatchWinner ? ["H", "D", "A"] : ["false", "true"];
    public static void Validate(PredictionValue v, EvaluationTarget target)
    {
        if (v.Probabilities is null || (Count(target) ? v.Probabilities.Count != 0 || v.ExpectedCount is null or < 0 or > int.MaxValue :
            v.ExpectedCount is not null || v.Probabilities.Count != Classes(target).Count || v.Probabilities.Any(p => p < 0 || p > 1) || v.Probabilities.Sum() != 1))
            throw new ArgumentException("Exact finite probabilities summing to one, or a nonnegative bounded expected count required.");
    }
    public static PredictionInput Input(FootballResultDatasetRow row, DatasetGovernanceRow? governance = null, string? targetTimePolicy = null)
    {
        var t = row.Metadata.PredictionCutoffUtc; var target = row.Metadata.Target;
        if (!Utc(t) || target.EventId != row.Metadata.EventId || target.EventId == Guid.Empty || target.HomeId == Guid.Empty || target.AwayId == Guid.Empty ||
            !PredictionTimeBoundary.Allows(targetTimePolicy, target.EventId, target.DateObservationId, target.EventDate, t, governance?.EventTimes ?? []) || EventDecision(target) == Guid.Empty ||
            target.InterpretationCutoffUtc > t || target.EvidenceCutoffUtc > t ||
            row.FeatureEvidence.Query.Mode != DatasetMode.HistoricalAsKnown || row.FeatureEvidence.Query.ReconstructionAtUtc is not null || row.FeatureEvidence.Query.AsOfUtc != t ||
            row.Features.TargetEventId != target.EventId || row.Features.PredictionCutoffUtc != t ||
            row.Metadata.History.Append(target).Any(e => e.DateAvailableUtc > t || e.DateRecordedUtc > t || e.RawRecordedUtc > t || e.InterpretationCutoffUtc > t || e.EvidenceCutoffUtc > t) ||
            row.FeatureEvidence.Results.Any(e => e.Observation.AvailableAtUtc > t || e.Observation.RecordedAtUtc > t ||
                e.Observation.EventId == target.EventId && FootballOutcomes.Derive(e.Observation) is not null)) throw new InvalidDataException("Prediction-time leakage or identity mismatch.");
        // Frozen decision/quality/policy clocks must also be known at prediction, including rejected history.
        foreach (var f in row.Metadata.History.Append(target).SelectMany(e => e.FrozenRecords).Concat(row.FeatureEvidence.Results.SelectMany(e => e.FrozenRecords)).Concat(governance?.FrozenRecords ?? []))
        {
            using var json = System.Text.Json.JsonDocument.Parse(f.CanonicalJson);
            KnownClocks(json.RootElement, t);
        }
        var hash = CanonicalDatasetJson.Fingerprint(new { SchemaVersion = 3, row.Metadata.Target, row.Metadata.PredictionCutoffUtc, row.FeatureEvidence, row.Features });
        var vector = FootballResultFeatures.Compute(target, t, row.FeatureEvidence.Results);
        if (hash != row.FeatureHash || CanonicalDatasetJson.Fingerprint(vector) != CanonicalDatasetJson.Fingerprint(row.Features)) throw new InvalidDataException("Feature hash/calculation mismatch.");
        return new(1, target.EventId, target.HomeId, target.AwayId, t, row.FeatureHash, row.Features,
            row.Metadata.History.Append(target).SelectMany(e => e.DecisionIds.Append(e.DateObservationId).Append(e.RawId))
                .Concat(row.FeatureEvidence.Results.SelectMany(e => e.IdentityDecisionIds.Concat(e.QualityIds).Append(e.Observation.Id).Append(e.Observation.RawId))).Distinct().Order().ToArray());
    }
    public static Guid EventDecision(DatasetEvidenceReference target)
    {
        foreach (var record in target.FrozenRecords.Where(r => r.Kind == "decision" && target.DecisionIds.Contains(r.Id)))
        {
            using var json = System.Text.Json.JsonDocument.Parse(record.CanonicalJson);
            if (json.RootElement.TryGetProperty("CanonicalSportingEventId", out var eventId) && eventId.ValueKind == System.Text.Json.JsonValueKind.String &&
                eventId.TryGetGuid(out var id) && id == target.EventId && json.RootElement.TryGetProperty("Status", out var status) && status.GetString() == "Resolved") return record.Id;
        }
        return Guid.Empty;
    }
    private static void KnownClocks(System.Text.Json.JsonElement json, DateTime t)
    {
        if (json.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in json.EnumerateObject())
            {
                if (p.Name is "RecordedAtUtc" or "RecordedUtc" or "AvailableAtUtc" or "ReviewedAtUtc" && p.Value.ValueKind == System.Text.Json.JsonValueKind.String && p.Value.TryGetDateTime(out var at) && at > t)
                    throw new InvalidDataException("Later interpretation cannot enter prediction inputs.");
                KnownClocks(p.Value, t);
            }
        else if (json.ValueKind == System.Text.Json.JsonValueKind.Array) foreach (var v in json.EnumerateArray()) KnownClocks(v, t);
    }
}
public sealed class SyntheticConstantPredictor : IHistoricalPredictionProvider
{
    public string Name => "synthetic-constant";
    public int Version => 1;
    public PredictionValue Predict(PredictionInput input, EvaluationTarget target)
    {
        if (input.Version != 1 || !BacktestRules.Hash(input.FeatureHash) || !Enum.IsDefined(target)) throw new ArgumentException("Supported canonical input required.");
        // Fixed explicit priors are fictional test baselines; no training, results or provider access.
        return BacktestRules.Count(target) ? new([], target == EvaluationTarget.FirstHalfTotalGoals ? 1m : 2m) :
            target == EvaluationTarget.MatchWinner ? new([0.4m, 0.3m, 0.3m], null) : new([0.5m, 0.5m], null);
    }
}
