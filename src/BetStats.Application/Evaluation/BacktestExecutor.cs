using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Domain.Coverage;

namespace BetStats.Application.Evaluation;

public sealed class BacktestExecutor(IHistoricalPredictionProvider predictor)
{
    public BacktestManifest Execute(BacktestDefinition definition, FootballResultSnapshot features, FootballResultManifest evaluationEvidence)
    {
        definition.Validate(); var d = features.Manifest.MetadataManifest.Definition;
        if (predictor.Name != definition.Predictor || predictor.Version != definition.PredictorVersion || features.Id != definition.DatasetId || features.Hash != definition.ExpectedDatasetHash ||
            evaluationEvidence.MetadataManifestHash != features.Manifest.MetadataManifestHash || evaluationEvidence.LabelAsOfUtc != definition.EvaluationCutoffUtc ||
            evaluationEvidence.Rows.Count != features.Manifest.Rows.Count || d.Mode != BetStats.Domain.Quality.DatasetMode.HistoricalAsKnown ||
            definition.Evaluations.Any(e => e.SportId != d.SportId) || definition.EvaluationCutoffUtc < d.AsOfUtc)
            throw new InvalidDataException("Backtest dataset, predictor, scope or historical feature boundary mismatch.");
        var predictions = new List<HistoricalPrediction>(); var samples = new List<BacktestSample>();
        foreach (var row in features.Manifest.Rows.OrderBy(r => r.Metadata.EventId))
        {
            var mg = features.Manifest.MetadataManifest.Governance?.Rows.SingleOrDefault(g => g.EventId == row.Metadata.EventId);
            var input = BacktestRules.Input(row, mg);
            var later = evaluationEvidence.Rows.Single(r => r.Metadata.EventId == input.EventId);
            if (later.FeatureHash != row.FeatureHash || CanonicalDatasetJson.Fingerprint(later.Metadata) != CanonicalDatasetJson.Fingerprint(row.Metadata)) throw new InvalidDataException("Evaluation cannot mutate prediction features.");
            foreach (var e in definition.Evaluations.OrderBy(e => e.Target))
            {
                var value = predictor.Predict(input, e.Target); BacktestRules.Validate(value, e.Target);
                var prediction = new HistoricalPrediction(1, input.EventId, input.CutoffUtc, predictor.Name, predictor.Version, features.Id, features.Hash,
                    input.FeatureHash, CanonicalDatasetJson.Fingerprint(input), e.Target, value, input.EvidenceIds);
                predictions.Add(prediction);
                var reasons = new List<string>(); var query = later.LabelEvidence.Query;
                if (query.AsOfUtc != definition.EvaluationCutoffUtc || query.Mode != e.Mode || query.ReconstructionAtUtc != e.ReconstructionUtc || query.IncludeSuperseded ||
                    query.EventId != input.EventId || query.SourceId != row.Metadata.Target.SourceId || query.CompetitionId != d.CompetitionId || query.SeasonId != d.SeasonId)
                    throw new InvalidDataException("Label interpretation boundary mismatch.");
                var labels = later.LabelEvidence.Results.Where(x => x.Eligible && x.Observation.EventId == input.EventId && x.Observation.SourceId == row.Metadata.Target.SourceId &&
                    x.Observation.HomeId == input.HomeId && x.Observation.AwayId == input.AwayId && x.Observation.EventDate == row.Metadata.Target.EventDate && FootballResultRules.LabelEligible(x.Observation.Value)).ToArray();
                if (later.LabelEvidence.Results.Any(x => x.Eligible && x.Observation.EventId == input.EventId && x.Observation.EventDate != row.Metadata.Target.EventDate)) reasons.Add("outcome_event_date_changed_since_prediction");
                if (labels.Length != 1 || later.LabelEvidence.Results.Any(x => !x.Eligible)) reasons.Add(labels.Length > 1 ? "outcome_conflict" : "eligible_outcome_missing_or_conflicting");
                var label = labels.Length == 1 ? labels[0] : null;
                var outcome = label is null ? null : FootballOutcomes.Derive(label.Observation);
                var rg = evaluationEvidence.ResultGovernance?.Rows.SingleOrDefault(g => g.EventId == input.EventId);
                var coverage = FeatureCoverage(e.Coverage, d, row.Metadata, mg);
                EvaluationEventEvidence? begin;
                if (e.CutoffPolicy == PredictionCutoffPolicy.BeforeCalendarDay)
                    begin = new(input.EventId, row.Metadata.Target.DateObservationId, BacktestRules.EventDecision(row.Metadata.Target),
                        new(row.Metadata.Target.EventDate, null, null, null, null, EventTimePrecision.DateOnly), true,
                        row.Metadata.Target.DateAvailableUtc, row.Metadata.Target.DateRecordedUtc, d.CalendarBasis);
                else
                {
                    var times = mg?.EventTimes.Where(t => t.EventId == input.EventId).ToArray() ?? [];
                    begin = times.Length == 1 ? new(input.EventId, times[0].Evidence.Id, times[0].IdentityDecisionId, times[0].Evidence.Value,
                        times[0].Resolution.UtcInstant is not null && times[0].Resolution.Precision is EventTimePrecision.Minute or EventTimePrecision.Second &&
                        times[0].Resolution.UtcInstant == EventTimeRules.Resolve(times[0].Evidence.Value).UtcInstant,
                        times[0].Evidence.AvailableAtUtc, times[0].Evidence.RecordedAtUtc, null) : null;
                }
                var ends = rg?.Ends.Where(x => x.Eligible && x.Evidence.ResultObservationId == label?.Observation.Id).ToArray() ?? [];
                EvaluationEndEvidence? end = ends.Length == 1 ? new(input.EventId, ends[0].Evidence.ResultObservationId, ends[0].Evidence.Id, ends[0].IdentityDecisionId,
                    ends[0].Evidence.Value, true, true, ends[0].Evidence.AvailableAtUtc, ends[0].Evidence.RecordedAtUtc, ends[0].Evidence.Version) : null;
                if (label is not null)
                {
                    var eligible = EvaluationContracts.Eligibility(e, input.CutoffUtc, row.Metadata.Target.DateAvailableUtc, row.Metadata.Target.DateRecordedUtc,
                        new(label.Observation.Id, label.Observation.AvailableAtUtc, label.Observation.RecordedAtUtc, label.Observation.Version), definition.EvaluationCutoffUtc, true,
                        new("backtest", coverage, [], [], []), begin, end, rg?.LabelCoverage.Status);
                    reasons.AddRange(eligible.Reasons);
                }
                else
                {
                    if (rg?.LabelCoverage.Status != BetStats.Domain.Football.ResultCoverageStatus.Complete) reasons.Add("independent_complete_result_coverage_required");
                    if (end is null) reasons.Add("justified_event_end_evidence_missing");
                    if (coverage != FeatureCoverageOutcome.Eligible) reasons.Add("complete_evaluation_coverage_required");
                }
                int? cls = e.Target switch
                {
                    EvaluationTarget.MatchWinner => outcome?.Winner switch { "HomeWin" => 0, "Draw" => 1, "AwayWin" => 2, _ => null },
                    EvaluationTarget.BothTeamsScoring => outcome?.BothTeamsScored is { } b ? b ? 1 : 0 : null,
                    EvaluationTarget.OverUnder25 => outcome?.Over2_5Goals is { } o ? o ? 1 : 0 : null,
                    EvaluationTarget.FirstHalfGoalOccurrence => outcome?.HalfTimeTotalGoals is { } h ? h > 0 ? 1 : 0 : null,
                    _ => null
                };
                int? count = e.Target == EvaluationTarget.TotalGoals ? outcome?.FullTimeTotalGoals : e.Target == EvaluationTarget.FirstHalfTotalGoals ? outcome?.HalfTimeTotalGoals : null;
                if (BacktestRules.Count(e.Target) ? count is null : cls is null) reasons.Add("target_label_missing");
                samples.Add(new(input.EventId, e.Target, prediction, label?.Observation.Id, label?.Observation.Version, reasons.Count == 0,
                    reasons.Distinct().Order(StringComparer.Ordinal).ToArray(), cls, count));
            }
        }
        return new(1, 1, definition, CanonicalDatasetJson.Fingerprint(definition), "historical-simulation-not-live-prediction", predictions, samples, evaluationEvidence,
            new(1, BacktestMetrics.Version, BacktestMetrics.Semantics, definition.Evaluations.OrderBy(e => e.Target).Select(e => BacktestMetrics.Compute(e, samples)).ToArray()));
    }
    private static FeatureCoverageOutcome FeatureCoverage(FeatureCoverageRequirement requirement, DatasetDefinition d, DatasetRow row, DatasetGovernanceRow? frozen)
    {
        if (frozen is null) return FeatureCoverageOutcome.UnknownCoverage;
        var day = DateOnly.FromDateTime(row.PredictionCutoffUtc);
        var start = requirement.LookbackDays is { } days ? day.AddDays(-days) : d.SeasonStart < day ? d.SeasonStart : day.AddDays(-1);
        foreach (var participant in new[] { row.Target.HomeId, row.Target.AwayId })
        {
            var selected = frozen.Coverage.Where(r => r.Query.Scope.SourceId == row.Target.SourceId && r.Query.Scope.SportId == d.SportId &&
                r.Query.Scope.CompetitionId == d.CompetitionId && r.Query.Scope.SeasonId == d.SeasonId && r.Query.Scope.ParticipantId == participant &&
                r.Query.Scope.CompetitionReference == d.CompetitionReference && r.Query.Scope.SeasonReference == d.SeasonReference && r.Query.Scope.EventType == "football-match" &&
                r.Query.Scope.Interval.Kind == IntervalKind.Calendar && r.Query.Scope.Interval.CalendarBasis == "UTC-calendar" &&
                r.Query.Scope.Interval.StartDate == start && r.Query.Scope.Interval.EndDate == day && r.Query.AsOfUtc == row.PredictionCutoffUtc &&
                r.Query.Mode == BetStats.Domain.Quality.DatasetMode.HistoricalAsKnown && r.Query.ReconstructionUtc is null && r.Query.Purpose == d.Purpose && r.Query.Context == d.Context &&
                requirement.ObservationTypes.Contains(r.Query.Scope.ObservationType)).ToArray();
            if (selected.Length == 0) return FeatureCoverageOutcome.UnknownCoverage;
            if (selected.GroupBy(r => r.Query.Scope.ObservationType).Any(group => group.Select(CanonicalDatasetJson.Fingerprint).Distinct().Count() > 1)) return FeatureCoverageOutcome.ConflictingCoverage;
            selected = selected.DistinctBy(r => r.Query.Scope.ObservationType).ToArray();
            // Observed-feature gates deliberately permit partial history; apply the stricter evaluation requirement separately.
            var gate = CoverageRules.Gate(requirement, selected);
            if (gate.Outcome != FeatureCoverageOutcome.Eligible) return gate.Outcome;
        }
        return FeatureCoverageOutcome.Eligible;
    }
}
