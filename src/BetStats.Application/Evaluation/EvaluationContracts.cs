using BetStats.Application.Coverage;
using BetStats.Domain.Quality;
using BetStats.Domain.Coverage;
using BetStats.Domain.Football;

namespace BetStats.Application.Evaluation;

public enum EvaluationTarget { MatchWinner, TotalGoals, BothTeamsScoring, FirstHalfGoalOccurrence }
public enum PredictionCutoffPolicy { BeforeCalendarDay, BeforeJustifiedKickoff }
public sealed record EvaluationMetricDefinition(string Name, int Version, string PredictionFormat, string LabelFormat, string ValidRange,
    string MissingLabelBehavior, int MinimumSamples, string Weighting, string Aggregation);
public sealed record OutcomeAvailability(Guid EvidenceId, DateTime AvailableUtc, DateTime RecordedUtc, int Version);
public sealed record EvaluationDefinition(int Version, Guid SportId, EvaluationTarget Target, DatasetMode Mode, DateTime? ReconstructionUtc,
    PredictionCutoffPolicy CutoffPolicy, TimeSpan PredictionHorizon, IReadOnlyList<string> RequiredEvidence, string OutcomeObservationType,
    int QualityVersion, FeatureCoverageRequirement Coverage, IReadOnlyList<EvaluationMetricDefinition> Metrics,
    string OutcomeAvailabilityRule = "after-prediction-and-recorded-by-evaluation-v1",
    string HorizonRule = "minimum-lead-time-v2")
{
    public void Validate()
    {
        if (Version < 1 || SportId == Guid.Empty || !Enum.IsDefined(Target) || !Enum.IsDefined(Mode) || !Enum.IsDefined(CutoffPolicy) ||
            PredictionHorizon <= TimeSpan.Zero || PredictionHorizon > TimeSpan.FromDays(365) || HorizonRule != "minimum-lead-time-v2" || QualityVersion != 1 || OutcomeAvailabilityRule != "after-prediction-and-recorded-by-evaluation-v1" ||
            RequiredEvidence is null || !new[] { "features", "event-time", "quality", "coverage", "source-policy" }.All(RequiredEvidence.Contains) ||
            OutcomeObservationType != Target.ToString() || Coverage is null || !Coverage.CompletenessRequired || Coverage.PartialAllowed ||
            Metrics is null || Metrics.Count is < 1 or > 5 || Metrics.Select(m => m.Name).Distinct().Count() != Metrics.Count ||
            Mode == DatasetMode.HistoricalAsKnown && ReconstructionUtc is not null ||
            Mode == DatasetMode.RetrospectiveReconstruction && ReconstructionUtc is not { Kind: DateTimeKind.Utc })
            throw new ArgumentException("Explicit versioned evaluation definition required; future outcomes are contracts only.");
        CoverageRules.ValidateRequirement(Coverage);
        foreach (var metric in Metrics)
        {
            if (!EvaluationContracts.Metrics.Contains(metric)) throw new ArgumentException("Unsupported metric semantics.");
            if ((Target == EvaluationTarget.TotalGoals) != (metric.Name == "mae")) throw new ArgumentException("Metric/target format mismatch.");
            if (Target == EvaluationTarget.MatchWinner && metric.Name == "calibration_error") throw new ArgumentException("Binary calibration contract cannot describe a three-class winner target.");
        }
    }
}
public sealed record EvaluationEligibility(bool Eligible, bool Retrospective, IReadOnlyList<string> Reasons,
    [property: System.Text.Json.Serialization.JsonIgnore] int SchemaVersion = 2)
{
    public int ContractVersion => SchemaVersion;
}
public sealed record EvaluationEventEvidence(Guid EventId, Guid EvidenceId, Guid IdentityDecisionId,
    EventTimeValue Value, bool SourceBound, DateTime AvailableUtc, DateTime RecordedUtc, string? CalendarBasis);
public sealed record EvaluationEndEvidence(Guid EventId, Guid ResultObservationId, Guid EvidenceId, Guid IdentityDecisionId,
    EventTimeValue Value, bool SourceBound, bool Eligible, DateTime AvailableUtc, DateTime RecordedUtc, int Version);
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
    public static EvaluationEligibility Eligibility(EvaluationDefinition definition, DateTime predictionCutoff, DateTime featureAvailable, DateTime featureRecorded,
        OutcomeAvailability label, DateTime evaluationCutoff, bool authorized, FeatureCoverageDecision coverage, EvaluationEventEvidence? eventEvidence = null,
        EvaluationEndEvidence? endEvidence = null, ResultCoverageStatus? resultCoverage = null)
    {
        definition.Validate(); var reasons = new List<string>();
        if (new[] { predictionCutoff, featureAvailable, featureRecorded, label.AvailableUtc, label.RecordedUtc, evaluationCutoff }.Any(t => t.Kind != DateTimeKind.Utc || t.Ticks % 10 != 0) ||
            evaluationCutoff < predictionCutoff || label.EvidenceId == Guid.Empty || label.Version < 1) throw new ArgumentException("UTC evidence/cutoffs and version required.");
        if (definition.ReconstructionUtc is { } r && (r < predictionCutoff || r > evaluationCutoff)) throw new ArgumentException("Reconstruction boundary must be explicit and within evaluation.");
        DateTime? boundary = null;
        if (eventEvidence is null || eventEvidence.EventId == Guid.Empty || eventEvidence.EvidenceId == Guid.Empty || eventEvidence.IdentityDecisionId == Guid.Empty || !eventEvidence.SourceBound)
            reasons.Add("justified_event_evidence_missing");
        else
        {
            if (eventEvidence.AvailableUtc.Kind != DateTimeKind.Utc || eventEvidence.RecordedUtc.Kind != DateTimeKind.Utc ||
                eventEvidence.AvailableUtc.Ticks % 10 != 0 || eventEvidence.RecordedUtc.Ticks % 10 != 0 ||
                eventEvidence.AvailableUtc > predictionCutoff || eventEvidence.RecordedUtc > predictionCutoff)
                reasons.Add("event_evidence_not_known_at_prediction");
            if (definition.CutoffPolicy == PredictionCutoffPolicy.BeforeJustifiedKickoff)
            {
                var resolution = EventTimeRules.Resolve(eventEvidence.Value);
                if (resolution.UtcInstant is null || eventEvidence.Value.Precision is not (EventTimePrecision.Minute or EventTimePrecision.Second))
                    reasons.Add("precise_justified_kickoff_required");
                else boundary = resolution.UtcInstant;
            }
            else if (eventEvidence.CalendarBasis != "UTC-calendar" || eventEvidence.Value.LocalDate is null)
                reasons.Add("explicit_utc_calendar_day_required");
            else boundary = eventEvidence.Value.LocalDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        }
        // Horizon v2 means minimum lead time, never a guessed timestamp from DateOnly.
        if (boundary is { } target)
        {
            if (predictionCutoff >= target) reasons.Add("prediction_not_before_event_boundary");
            if (target - predictionCutoff < definition.PredictionHorizon) reasons.Add("minimum_prediction_horizon_not_met");
        }
        if (featureAvailable > predictionCutoff) reasons.Add("feature_first_available_after_prediction");
        if (featureRecorded > predictionCutoff) reasons.Add("feature_first_recorded_after_prediction");
        if (label.AvailableUtc <= predictionCutoff) reasons.Add("outcome_not_separated_from_prediction");
        if (label.AvailableUtc > evaluationCutoff || label.RecordedUtc > evaluationCutoff) reasons.Add("outcome_not_available_for_evaluation");
        if (!authorized) reasons.Add("source_permission_denied");
        if (coverage.Outcome != FeatureCoverageOutcome.Eligible) reasons.Add("complete_evaluation_coverage_required");
        if (label.Version > definition.Version) reasons.Add("outcome_correction_requires_new_evaluation_version");
        if (definition.Version >= 3)
        {
            if (resultCoverage is not ResultCoverageStatus.Complete) reasons.Add("independent_complete_result_coverage_required");
            if (endEvidence is null || !endEvidence.SourceBound || !endEvidence.Eligible || endEvidence.EventId != eventEvidence?.EventId ||
                endEvidence.ResultObservationId != label.EvidenceId || endEvidence.EvidenceId == Guid.Empty || endEvidence.IdentityDecisionId == Guid.Empty || endEvidence.Version < 1)
                reasons.Add("justified_event_end_evidence_missing");
            else
            {
                var end = EventTimeRules.Resolve(endEvidence.Value);
                if (endEvidence.AvailableUtc.Kind != DateTimeKind.Utc || endEvidence.RecordedUtc.Kind != DateTimeKind.Utc ||
                    endEvidence.AvailableUtc.Ticks % 10 != 0 || endEvidence.RecordedUtc.Ticks % 10 != 0 ||
                    endEvidence.AvailableUtc > evaluationCutoff || endEvidence.RecordedUtc > evaluationCutoff)
                    reasons.Add("event_end_not_known_at_evaluation");
                if (end.UtcInstant is not { } finished || endEvidence.Value.Precision is not (EventTimePrecision.Minute or EventTimePrecision.Second))
                    reasons.Add("precise_event_end_required");
                else if (finished <= predictionCutoff || finished > label.AvailableUtc || finished > endEvidence.AvailableUtc || boundary is { } beginning && finished <= beginning)
                    reasons.Add("event_end_temporal_conflict");
                if (endEvidence.Version > definition.Version) reasons.Add("event_end_correction_requires_new_evaluation_version");
            }
        }
        return new(reasons.Count == 0, definition.Mode == DatasetMode.RetrospectiveReconstruction, reasons, definition.Version >= 3 ? 3 : 2);
    }
}
