using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Domain.Coverage;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Quality;
using BetStats.Domain.Observations;

namespace BetStats.UnitTests;

public sealed class BacktestTests
{
    private static readonly DateTime T = new(2031, 1, 2, 12, 0, 0, DateTimeKind.Utc);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    internal static EvaluationDefinition Evaluation(EvaluationTarget t) => new(3, Id(1), t, DatasetMode.HistoricalAsKnown, null,
        PredictionCutoffPolicy.BeforeCalendarDay, TimeSpan.FromHours(1), ["features", "event-time", "quality", "coverage", "source-policy"], t.ToString(), 1,
        new("result", 1, [ObservationType.EventDate], 30, false, "Completed", 1, false, true),
        EvaluationContracts.Metrics.Where(m => BacktestRules.Count(t) ? m.Name == "mae" : t == EvaluationTarget.MatchWinner ? m.Name != "mae" && m.Name != "calibration_error" : m.Name != "mae").ToArray());
    private static (BacktestDefinition Definition, FootballResultSnapshot Features, FootballResultManifest Evidence) Scenario()
    {
        var target = new DatasetEvidenceReference(Id(2), Id(3), Id(4), Id(5), Id(6), [], [Id(7)], Id(8), null, Id(9), new('a', 64), T, T, T,
            new(2031, 1, 3), "Scheduled", [], [], T, T, [new("decision", Id(7), System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new { CanonicalSportingEventId = Id(3), Status = "Resolved", RecordedAtUtc = T })))]);
        var def = new DatasetDefinition(2, Id(1), Id(10), Id(11), "FICT", "fiction", new(2031, 1, 1), new(2031, 12, 31), T, DatasetMode.HistoricalAsKnown, null,
            1, 2, DataPurpose.InternalAnalytics, new(), "UTC-calendar", "eligible-observed-metadata-v1", "fail-closed-v1", [new(Id(8), T)]);
        var meta = new DatasetRow(Id(3), T, target, [], [], new(2, Id(3), T, []), new('b', 64));
        var q = new FootballResultQuery(Id(10), Id(11), T, DataPurpose.InternalAnalytics, new(), SourceId: Id(2));
        var feature = new FootballResultReport(q, []); var vector = FootballResultFeatures.Compute(target, T, []);
        var row = new FootballResultDatasetRow(meta, feature, new(q with { EventId = Id(3) }, []), vector, [], "");
        row = row with { FeatureHash = CanonicalDatasetJson.Fingerprint(new { SchemaVersion = 3, row.Metadata.Target, row.Metadata.PredictionCutoffUtc, row.FeatureEvidence, row.Features }) };
        var coverage = new[] { Id(4), Id(5) }.Select(participant => new CoverageReport(new(new(Id(2), Id(1), Id(10), Id(11), participant, "football-match", ObservationType.EventDate,
            "FICT", "fiction", new(IntervalKind.Calendar, null, null, DateOnly.FromDateTime(T).AddDays(-30), DateOnly.FromDateTime(T), "UTC-calendar")),
            T, DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new()), CoverageStatus.VerifiedComplete, true, [], [], [], [], [], [])).ToArray();
        var mg = new DatasetGovernanceRow(Id(3), T, 2, coverage, [new("observed", FeatureCoverageOutcome.EligibleWithPartialCoverage, [], [], [])], [], new(null, EventTimePrecision.DateOnly, "calendar"), []);
        var mm = new DatasetManifest(2, 1, CanonicalDatasetJson.Fingerprint(def), def, [], [meta], new(2, [mg]));
        var manifest = new FootballResultManifest(3, 3, 1, mm, CanonicalDatasetJson.Fingerprint(mm), T, [row]);
        var snapshot = new FootballResultSnapshot(Id(12), CanonicalDatasetJson.Fingerprint(manifest), manifest);
        var result = CanonicalDatasetJson.Deserialize<FootballResultObservation>(CanonicalDatasetJson.Serialize(new {
            Id = Id(13), SourceId = Id(2), EventId = Id(3), HomeId = Id(4), AwayId = Id(5), Version = 1,
            Value = new FootballResultValue(FootballMatchStatus.Finished, FootballScoreBasis.RegulationTime, new(2, 1), new(1, 0)),
            EventDate = new DateOnly(2031, 1, 3), AvailableAtUtc = T.AddDays(1), RecordedAtUtc = T.AddDays(1), RetrievedAtUtc = T.AddDays(1) }));
        var label = new FootballResultEvidence(result, true, [], [Id(7)], [], [], FootballOutcomes.Derive(result));
        var eq = q with { AsOfUtc = T.AddDays(2), EventId = Id(3) };
        var end = CanonicalDatasetJson.Deserialize<EventEndEvidence>(CanonicalDatasetJson.Serialize(new {
            Id = Id(14), SourceId = Id(2), ResultObservationId = Id(13), RawId = Id(15), RawHash = new string('a', 64), PolicyId = Id(16), IdentityDecisionId = Id(7),
            Value = new EventTimeValue(new(2031, 1, 3), new(2, 0), null, 0, null, EventTimePrecision.Minute), Version = 1,
            AvailableAtUtc = T.AddDays(1), RecordedAtUtc = T.AddDays(1), RetrievedAtUtc = T.AddDays(1), OperatorId = "fixture", Reason = "Pure historical scenario" }));
        var scope = new ResultCoverageScope(Id(2), Id(10), Id(11), null, "FICT", "fiction", new(IntervalKind.Calendar, null, null, new(2031, 1, 3), new(2031, 1, 4), "UTC-calendar"));
        var rc = new ResultCoverageReport(1, new(scope, eq), ResultCoverageStatus.Complete, [], [Id(13)], []);
        var rg = new ResultDatasetGovernance(1, [new(Id(3), rc, rc, [new(end, Id(3), Id(7), EventTimeRules.Resolve(end.Value), true, [])])]);
        var evidence = manifest with { LabelAsOfUtc = T.AddDays(2), Rows = [row with { LabelEvidence = new(eq, [label]), Labels = [label.Labels!] }], ResultGovernance = rg };
        var definition = new BacktestDefinition(1, Id(12), snapshot.Hash, "synthetic-constant", 1, T.AddDays(2), Enum.GetValues<EvaluationTarget>().Select(Evaluation).ToArray());
        return (definition, snapshot, evidence);
    }
    [Fact] public void All_six_targets_evaluate_and_reproduce_without_training()
    {
        var s = Scenario(); var engine = new BacktestExecutor(new SyntheticConstantPredictor()); var one = engine.Execute(s.Definition, s.Features, s.Evidence);
        Assert.Equal(6, one.Predictions.Count); Assert.All(one.Samples, x => Assert.True(x.Eligible, string.Join(',', x.Reasons)));
        Assert.Equal(CanonicalDatasetJson.Serialize(one), CanonicalDatasetJson.Serialize(engine.Execute(s.Definition, s.Features, s.Evidence)));
        Assert.All(one.Report.Targets.SelectMany(t => t.Metrics), m => { Assert.Equal(1, m.Denominator); Assert.False(m.MinimumSamplesMet); });
        Assert.Equal(1m, one.Report.Targets.Single(t => t.Target == EvaluationTarget.TotalGoals).Metrics.Single().Value);
        Assert.Equal(0m, one.Report.Targets.Single(t => t.Target == EvaluationTarget.FirstHalfTotalGoals).Metrics.Single().Value);
    }
    [Theory] [InlineData("feature")] [InlineData("review")] [InlineData("hash")] [InlineData("reinterpretation")]
    public void Leakage_is_a_hard_failure(string failure)
    {
        var s = Scenario(); var row = s.Features.Manifest.Rows.Single();
        if (failure == "feature") row = row with { Metadata = row.Metadata with { Target = row.Metadata.Target with { DateAvailableUtc = T.AddSeconds(1) } } };
        if (failure == "review") row = row with { Metadata = row.Metadata with { Target = row.Metadata.Target with { FrozenRecords = [new("decision", Id(7), System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new { CanonicalSportingEventId = Id(3), Status = "Resolved", RecordedAtUtc = T.AddSeconds(1) })))] } } };
        if (failure == "hash") row = row with { FeatureHash = new('c', 64) };
        if (failure == "reinterpretation") row = row with { FeatureEvidence = row.FeatureEvidence with { Query = row.FeatureEvidence.Query with { Mode = DatasetMode.RetrospectiveReconstruction, ReconstructionAtUtc = T.AddDays(1) } } };
        Assert.Throws<InvalidDataException>(() => BacktestRules.Input(row));
    }
    [Theory] [InlineData("missing")] [InlineData("partial")] [InlineData("conflict")] [InlineData("end")] [InlineData("late-label")] [InlineData("correction-version")] [InlineData("rescheduled")]
    public void Insufficient_evaluation_evidence_excludes_never_invents_labels(string failure)
    {
        var s = Scenario(); var evidence = s.Evidence; var row = evidence.Rows.Single(); var rg = evidence.ResultGovernance!.Rows.Single();
        if (failure == "missing") row = row with { LabelEvidence = row.LabelEvidence with { Results = [] }, Labels = [] };
        if (failure == "partial") rg = rg with { LabelCoverage = rg.LabelCoverage with { Status = ResultCoverageStatus.Partial } };
        if (failure == "conflict") row = row with { LabelEvidence = row.LabelEvidence with { Results = [row.LabelEvidence.Results[0], row.LabelEvidence.Results[0]] } };
        if (failure == "end") rg = rg with { Ends = [] };
        if (failure == "late-label" || failure == "correction-version" || failure == "rescheduled")
        {
            var original = row.LabelEvidence.Results[0]; var json = System.Text.Json.Nodes.JsonNode.Parse(CanonicalDatasetJson.Serialize(original.Observation))!;
            if (failure == "late-label") json["RecordedAtUtc"] = "2031-01-06T12:00:00.000000Z"; else if (failure == "rescheduled") json["EventDate"] = "2031-01-04"; else json["Version"] = 4;
            var changed = CanonicalDatasetJson.Deserialize<FootballResultObservation>(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
            row = row with { LabelEvidence = row.LabelEvidence with { Results = [original with { Observation = changed, Labels = FootballOutcomes.Derive(changed) }] } };
        }
        evidence = evidence with { Rows = [row], ResultGovernance = new(1, [rg]) };
        var report = new BacktestExecutor(new SyntheticConstantPredictor()).Execute(s.Definition, s.Features, evidence);
        Assert.All(report.Samples, x => Assert.False(x.Eligible)); Assert.All(report.Report.Targets.SelectMany(t => t.Metrics), m => { Assert.Equal(0, m.Denominator); Assert.Null(m.Value); });
    }
    [Fact] public void Later_labels_and_corrections_do_not_change_predictions()
    {
        var s = Scenario(); var engine = new BacktestExecutor(new SyntheticConstantPredictor()); var one = engine.Execute(s.Definition, s.Features, s.Evidence);
        var row = s.Evidence.Rows.Single(); var changed = s.Evidence with { Rows = [row with { LabelEvidence = row.LabelEvidence with { Results = [] }, Labels = [] }] };
        var two = engine.Execute(s.Definition, s.Features, changed);
        Assert.Equal(CanonicalDatasetJson.Serialize(one.Predictions), CanonicalDatasetJson.Serialize(two.Predictions));
        Assert.NotEqual(CanonicalDatasetJson.Fingerprint(one.Report), CanonicalDatasetJson.Fingerprint(two.Report));
    }
    private static BacktestSample Sample(int n, EvaluationTarget target, PredictionValue value, int? cls = null, int? count = null) =>
        new(Id(100 + n), target, new(1, Id(100 + n), T, "synthetic-constant", 1, Id(12), new('a', 64), new('b', 64), new('c', 64), target, value, []), Id(13), 1, true, [], cls, count);
    [Fact] public void Binary_scores_use_two_classes_natural_log_and_exact_denominators()
    {
        var t = EvaluationTarget.BothTeamsScoring; var rows = new[] { Sample(1, t, new([0.25m, 0.75m], null), 1), Sample(2, t, new([0.5m, 0.5m], null), 1) };
        var r = BacktestMetrics.Compute(Evaluation(t), rows);
        Assert.Equal(0.3125m, r.Metrics.Single(m => m.Name == "brier").Value);
        Assert.Equal(0.5m, r.Metrics.Single(m => m.Name == "accuracy").Value); // tie chooses false
        Assert.Equal(0.375m, r.Metrics.Single(m => m.Name == "calibration_error").Value);
        Assert.InRange(r.Metrics.Single(m => m.Name == "log_loss").Value!.Value, 0.490414626505m, 0.490414626507m);
    }
    [Fact] public void Zero_probability_means_infinity_without_epsilon_and_one_enters_final_bin()
    {
        var t = EvaluationTarget.BothTeamsScoring; var r = BacktestMetrics.Compute(Evaluation(t), [Sample(1, t, new([1m, 0m], null), 1)]);
        var loss = r.Metrics.Single(m => m.Name == "log_loss"); Assert.True(loss.PositiveInfinity); Assert.Null(loss.Value);
        Assert.Equal(1, r.Calibration[0].Bins[9].Count); Assert.Equal(0, r.Calibration[0].Bins[0].Count);
        Assert.Equal(2m, r.Metrics.Single(m => m.Name == "brier").Value);
        Assert.DoesNotContain("NaN", System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(r)));
    }
    [Theory] [InlineData("negative")] [InlineData("sum")] [InlineData("count")] [InlineData("mixed")]
    public void Probability_validation_rejects_invalid_formats(string failure)
    {
        var v = failure switch { "negative" => new PredictionValue([-0.1m, 1.1m], null), "sum" => new([0.4m, 0.4m], null), "count" => new([1m], null), _ => new([0.5m, 0.5m], 2m) };
        Assert.Throws<ArgumentException>(() => BacktestRules.Validate(v, EvaluationTarget.BothTeamsScoring));
    }
    [Fact] public void Hundred_sample_threshold_and_sample_order_are_explicit()
    {
        var t = EvaluationTarget.MatchWinner; var rows = Enumerable.Range(0, 100).Select(i => Sample(i, t, new([0.4m, 0.3m, 0.3m], null), 0)).ToArray();
        var one = BacktestMetrics.Compute(Evaluation(t), rows); var two = BacktestMetrics.Compute(Evaluation(t), rows.Reverse().ToArray());
        Assert.Equal(CanonicalDatasetJson.Serialize(one), CanonicalDatasetJson.Serialize(two)); Assert.All(one.Metrics, m => Assert.True(m.MinimumSamplesMet));
        Assert.Equal(0.54m, one.Metrics.Single(m => m.Name == "brier").Value);
        Assert.Throws<ArgumentException>(() => BacktestMetrics.Compute(Evaluation(t), [rows[0], rows[0]]));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Fencing_checks_actual_database_clock_owner_and_lease(bool expired)
    {
        Assert.Equal(!expired, OperationFencing.Owns(ResultOperationStatus.Running, Id(1), Id(1), expired ? T : T.AddTicks(10), T));
        Assert.False(OperationFencing.Owns(ResultOperationStatus.Running, Id(1), Id(2), T.AddHours(1), T));
        Assert.False(OperationFencing.Owns(ResultOperationStatus.Succeeded, Id(1), Id(1), T.AddHours(1), T));
    }
    [Fact] public void Target_outcome_cannot_be_a_prediction_input_even_when_already_known()
    {
        var s = Scenario(); var row = s.Features.Manifest.Rows.Single(); var label = s.Evidence.Rows[0].LabelEvidence.Results[0];
        var json = System.Text.Json.Nodes.JsonNode.Parse(CanonicalDatasetJson.Serialize(label.Observation))!;
        json["AvailableAtUtc"] = "2031-01-02T12:00:00.000000Z"; json["RecordedAtUtc"] = "2031-01-02T12:00:00.000000Z";
        var observation = CanonicalDatasetJson.Deserialize<FootballResultObservation>(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        row = row with { FeatureEvidence = row.FeatureEvidence with { Results = [label with { Observation = observation }] } };
        Assert.Throws<InvalidDataException>(() => BacktestRules.Input(row));
    }
    [Fact] public void Complete_observed_feature_coverage_is_rechecked_against_evaluation_dimensions()
    {
        var s = Scenario(); var wrong = s.Definition with { Evaluations = s.Definition.Evaluations.Select(e => e with { Coverage = e.Coverage with { ObservationTypes = [ObservationType.EventStatus] } }).ToArray() };
        var result = new BacktestExecutor(new SyntheticConstantPredictor()).Execute(wrong, s.Features, s.Evidence);
        Assert.All(result.Samples, sample => Assert.Contains("complete_evaluation_coverage_required", sample.Reasons));
        var row = s.Features.Manifest.Rows[0]; var governance = s.Features.Manifest.MetadataManifest.Governance!.Rows[0] with {
            FrozenRecords = [new("coverage-review", Id(30), System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new { RecordedAtUtc = T.AddSeconds(1) })))] };
        Assert.Throws<InvalidDataException>(() => BacktestRules.Input(row, governance));
    }
    [Fact] public void Precise_kickoff_policy_cannot_estimate_kickoff_from_date_only()
    {
        var s = Scenario(); var definition = s.Definition with { Evaluations = s.Definition.Evaluations.Select(e => e with { CutoffPolicy = PredictionCutoffPolicy.BeforeJustifiedKickoff }).ToArray() };
        var manifest = new BacktestExecutor(new SyntheticConstantPredictor()).Execute(definition, s.Features, s.Evidence);
        Assert.All(manifest.Samples, sample => { Assert.False(sample.Eligible); Assert.Contains("justified_event_evidence_missing", sample.Reasons); });
    }
    [Fact] public void Precise_kickoff_uses_only_the_frozen_source_time_known_at_prediction()
    {
        var s = Scenario(); var value = new EventTimeValue(new(2031, 1, 3), new(1, 0), null, 0, null, EventTimePrecision.Minute);
        var time = CanonicalDatasetJson.Deserialize<EventTimeEvidence>(CanonicalDatasetJson.Serialize(new { Id = Id(20), SourceId = Id(2), ProviderIdentityId = Id(6),
            DateObservationId = Id(8), RawId = Id(21), RawHash = new string('a', 64), PolicyId = Id(16), Value = value, Version = 1,
            EvidenceReference = "synthetic:historical-kickoff", RetrievedAtUtc = T, AvailableAtUtc = T, RecordedAtUtc = T, OperatorId = "fixture", Reason = "Pure source time" }));
        var old = s.Features.Manifest; var metadata = old.MetadataManifest;
        metadata = metadata with { Governance = new(2, [metadata.Governance!.Rows[0] with { EventTimes = [new(time, Id(7), Id(3), EventTimeRules.Resolve(value))] }]) };
        var updated = old with { MetadataManifest = metadata, MetadataManifestHash = CanonicalDatasetJson.Fingerprint(metadata) };
        var features = s.Features with { Manifest = updated, Hash = CanonicalDatasetJson.Fingerprint(updated) };
        var evidence = s.Evidence with { MetadataManifest = metadata, MetadataManifestHash = updated.MetadataManifestHash };
        var definition = s.Definition with { ExpectedDatasetHash = features.Hash, Evaluations = s.Definition.Evaluations.Select(e => e with { CutoffPolicy = PredictionCutoffPolicy.BeforeJustifiedKickoff }).ToArray() };
        var report = new BacktestExecutor(new SyntheticConstantPredictor()).Execute(definition, features, evidence);
        Assert.All(report.Samples, sample => Assert.True(sample.Eligible, string.Join(',', sample.Reasons)));
        var invalid = metadata.Governance!.Rows[0].EventTimes[0] with { Resolution = new(null, EventTimePrecision.Minute, "unverified_operator_time_assertion") };
        metadata = metadata with { Governance = new(2, [metadata.Governance.Rows[0] with { EventTimes = [invalid] }]) };
        updated = updated with { MetadataManifest = metadata, MetadataManifestHash = CanonicalDatasetJson.Fingerprint(metadata) };
        features = features with { Manifest = updated, Hash = CanonicalDatasetJson.Fingerprint(updated) };
        evidence = evidence with { MetadataManifest = metadata, MetadataManifestHash = updated.MetadataManifestHash };
        report = new BacktestExecutor(new SyntheticConstantPredictor()).Execute(definition with { ExpectedDatasetHash = features.Hash }, features, evidence);
        Assert.All(report.Samples, sample => { Assert.False(sample.Eligible); Assert.Contains("justified_event_evidence_missing", sample.Reasons); });
    }
    [Fact] public void Explicit_higher_definition_version_admits_corrections_without_rewriting_predictions()
    {
        var s = Scenario(); var row = s.Evidence.Rows[0]; var original = row.LabelEvidence.Results[0];
        var json = System.Text.Json.Nodes.JsonNode.Parse(CanonicalDatasetJson.Serialize(original.Observation))!;
        json["Version"] = 4; json["Value"]!["FullTime"]!["Home"] = 1; json["Value"]!["FullTime"]!["Away"] = 3;
        var corrected = CanonicalDatasetJson.Deserialize<FootballResultObservation>(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        var later = s.Evidence with { Rows = [row with { LabelEvidence = row.LabelEvidence with { Results = [original with { Observation = corrected, Labels = FootballOutcomes.Derive(corrected) }] } }] };
        var engine = new BacktestExecutor(new SyntheticConstantPredictor()); var before = engine.Execute(s.Definition, s.Features, s.Evidence);
        var denied = engine.Execute(s.Definition, s.Features, later); Assert.All(denied.Samples, sample => Assert.False(sample.Eligible));
        var versioned = s.Definition with { Evaluations = s.Definition.Evaluations.Select(e => e with { Version = 4 }).ToArray() };
        var accepted = engine.Execute(versioned, s.Features, later); Assert.All(accepted.Samples, sample => Assert.True(sample.Eligible));
        Assert.Equal(CanonicalDatasetJson.Serialize(before.Predictions), CanonicalDatasetJson.Serialize(accepted.Predictions));
        Assert.Equal(2, accepted.Samples.Single(sample => sample.Target == EvaluationTarget.MatchWinner).ClassLabel);
        Assert.NotEqual(CanonicalDatasetJson.Fingerprint(before), CanonicalDatasetJson.Fingerprint(accepted));
    }
}
