using BetStats.Application.Coverage;
using BetStats.Domain.Quality;

namespace BetStats.Application.Evaluation;

public enum EvaluationTarget { MatchWinner, TotalGoals, BothTeamsScoring, FirstHalfGoalOccurrence }
public enum PredictionCutoffPolicy { BeforeCalendarDay, BeforeJustifiedKickoff }
public sealed record EvaluationMetricDefinition(string Name, int Version, string PredictionFormat, string LabelFormat, string ValidRange,
    string MissingLabelBehavior, int MinimumSamples, string Weighting, string Aggregation);
public sealed record OutcomeAvailability(Guid EvidenceId, DateTime AvailableUtc, DateTime RecordedUtc, int Version);
public sealed record EvaluationDefinition(int Version, Guid SportId, EvaluationTarget Target, DatasetMode Mode, DateTime? ReconstructionUtc,
    PredictionCutoffPolicy CutoffPolicy, TimeSpan PredictionHorizon, IReadOnlyList<string> RequiredEvidence, string OutcomeObservationType,
    int QualityVersion, FeatureCoverageRequirement Coverage, IReadOnlyList<EvaluationMetricDefinition> Metrics)
{
    public void Validate()
    {
        if (Version < 1 || SportId == Guid.Empty || !Enum.IsDefined(Target) || !Enum.IsDefined(Mode) || !Enum.IsDefined(CutoffPolicy) ||
            PredictionHorizon <= TimeSpan.Zero || PredictionHorizon > TimeSpan.FromDays(365) || QualityVersion != 1 ||
            RequiredEvidence is null || !new[] { "features", "event-time", "quality", "coverage", "source-policy" }.All(RequiredEvidence.Contains) ||
            OutcomeObservationType != Target.ToString() || Coverage is null || !Coverage.CompletenessRequired || Coverage.PartialAllowed ||
            Metrics is null || Metrics.Count is < 1 or > 5 || Metrics.Select(m => m.Name).Distinct().Count() != Metrics.Count ||
            Mode == DatasetMode.HistoricalAsKnown && ReconstructionUtc is not null ||
            Mode == DatasetMode.RetrospectiveReconstruction && ReconstructionUtc is not { Kind: DateTimeKind.Utc })
            throw new ArgumentException("Explicit versioned evaluation definition required; future outcomes are contracts only.");
        foreach (var metric in Metrics)
        {
            if (!EvaluationContracts.Metrics.Contains(metric)) throw new ArgumentException("Unsupported metric semantics.");
            if ((Target == EvaluationTarget.TotalGoals) != (metric.Name == "mae")) throw new ArgumentException("Metric/target format mismatch.");
            if (Target == EvaluationTarget.MatchWinner && metric.Name == "calibration_error") throw new ArgumentException("Binary calibration contract cannot describe a three-class winner target.");
        }
    }
}
public sealed record EvaluationEligibility(bool Eligible, bool Retrospective, IReadOnlyList<string> Reasons);
public static class EvaluationContracts
{
    public static IReadOnlyList<EvaluationMetricDefinition> Metrics { get; } = [
        new("log_loss",1,"categorical-probabilities; finite; sum=1","categorical-one-hot","p in [0,1]; zero true-class p means positive infinity","exclude-and-report",100,"nonnegative finite weights; positive sum","global weighted mean; no epsilon invented"),
        new("brier",1,"categorical-probabilities; finite; sum=1","categorical-one-hot","p in [0,1]; categorical score [0,2]","exclude-and-report",100,"nonnegative finite weights; positive sum","global weighted mean of sum squared class errors"),
        new("accuracy",1,"explicit categorical class; tie policy required","categorical-class","score [0,1]","exclude-and-report",100,"nonnegative finite weights; positive sum","global weighted fraction correct"),
        new("calibration_error",1,"binary probability; finite","binary-label","p in [0,1]; error [0,1]","exclude-and-report",100,"nonnegative finite weights; positive sum","10 fixed equal-width bins; rightmost includes 1; weighted absolute bin error"),
        new("mae",1,"finite nonnegative expected-count","nonnegative integer count","error >=0","exclude-and-report",100,"nonnegative finite weights; positive sum","global weighted absolute error")];
    public static bool CanUseAsFeature(OutcomeAvailability evidence, DateTime predictionCutoff) => evidence.EvidenceId != Guid.Empty &&
        evidence.Version > 0 && evidence.AvailableUtc.Kind == DateTimeKind.Utc && evidence.RecordedUtc.Kind == DateTimeKind.Utc &&
        predictionCutoff.Kind == DateTimeKind.Utc && evidence.AvailableUtc <= predictionCutoff && evidence.RecordedUtc <= predictionCutoff;
    public static EvaluationEligibility Eligibility(EvaluationDefinition definition, DateTime predictionCutoff, DateTime featureAvailable,
        OutcomeAvailability label, DateTime evaluationCutoff, bool authorized, FeatureCoverageDecision coverage)
    {
        definition.Validate(); var reasons = new List<string>();
        if (new[] { predictionCutoff, featureAvailable, label.AvailableUtc, label.RecordedUtc, evaluationCutoff }.Any(t => t.Kind != DateTimeKind.Utc || t.Ticks % 10 != 0) ||
            evaluationCutoff < predictionCutoff || label.EvidenceId == Guid.Empty || label.Version < 1) throw new ArgumentException("UTC evidence/cutoffs and version required.");
        if (definition.ReconstructionUtc is { } r && (r < predictionCutoff || r > evaluationCutoff)) throw new ArgumentException("Reconstruction boundary must be explicit and within evaluation.");
        if (featureAvailable > predictionCutoff) reasons.Add("feature_first_available_after_prediction");
        if (label.AvailableUtc <= predictionCutoff) reasons.Add("outcome_not_separated_from_prediction");
        if (label.AvailableUtc > evaluationCutoff || label.RecordedUtc > evaluationCutoff) reasons.Add("outcome_not_available_for_evaluation");
        if (!authorized) reasons.Add("source_permission_denied");
        if (coverage.Outcome != FeatureCoverageOutcome.Eligible) reasons.Add("complete_evaluation_coverage_required");
        if (label.Version > definition.Version) reasons.Add("outcome_correction_requires_new_evaluation_version");
        return new(reasons.Count == 0, definition.Mode == DatasetMode.RetrospectiveReconstruction, reasons);
    }
}
