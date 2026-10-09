using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Models;

namespace BetStats.UnitTests;

public sealed class FootballModelTests
{
    private static readonly DateTime T = new(2031, 3, 12, 12, 0, 0, DateTimeKind.Utc);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    internal static PredictionInput Input(int matches = 3, bool half = true)
    {
        var history = Enumerable.Range(0, matches).Select(i => new ModelMatch(Id(10 + i), Id(2), Id(3), Id(4), DateOnly.FromDateTime(T.AddDays(-i - 1)),
            2, 1, half ? 1 : null, half ? 0 : null, Id(30 + i), 1, T.AddDays(-i - 1), T.AddDays(-i - 1), [Id(50 + i)])).ToArray();
        return new(2, Id(1), Id(2), Id(3), T, new('a', 64), new(3, Id(1), T, "partial", history.Select(m => m.ObservationId).ToArray(), []), [Id(5)], new(1, Id(4), history));
    }
    private static FootballModelDefinition Definition(FootballModelKind kind = FootballModelKind.Poisson) => new(1, kind, new(HomeAdvantage: 0), new(HomeMultiplier: 1));
    [Fact] public void Elo_expected_score_and_single_update_are_exact_at_equal_ratings()
    {
        Assert.Equal(.5m, FootballPredictor.Expected(1500, 1500, 400));
        var p = new FootballPredictor(Definition(FootballModelKind.Elo)).Simulate(Input(1));
        Assert.Equal(1510, p.HomeRating); Assert.Equal(1490, p.AwayRating);
        Assert.Equal(3000, p.HomeRating + p.AwayRating); Assert.False(p.Warmed);
    }
    [Fact] public void Elo_draw_and_same_day_batch_are_order_independent()
    {
        var i = Input(2); var matches = i.History!.Matches.Select(m => m with { EventDate = DateOnly.FromDateTime(T.AddDays(-1)) }).ToArray();
        var predictor = new FootballPredictor(Definition(FootballModelKind.Elo));
        var a = predictor.Simulate(i with { History = i.History with { Matches = matches } });
        var b = predictor.Simulate(i with { History = i.History with { Matches = matches.Reverse().ToArray() } });
        Assert.Equal(a.HomeRating, b.HomeRating); Assert.Equal(1520, a.HomeRating); Assert.Equal(1480, a.AwayRating);
        var draw = predictor.Simulate(Input(1) with { History = new(1, Id(4), [Input(1).History!.Matches[0] with { HomeGoals = 1, AwayGoals = 1 }]) });
        Assert.Equal(1500, draw.HomeRating);
    }
    [Theory] [InlineData(0)] [InlineData(1)]
    public void Explicit_season_reset_or_retention_controls_target_rating(int retain)
    {
        var p = new FootballPredictor(Definition(FootballModelKind.Elo) with { Elo = new(HomeAdvantage: 0, SeasonRetention: retain) });
        var result = p.Simulate(Input(1) with { History = Input(1).History! with { SeasonId = Id(99) } });
        Assert.Equal(1500 + 10 * retain, result.HomeRating);
    }
    [Theory] [InlineData(FootballModelKind.Elo)] [InlineData(FootballModelKind.Poisson)] [InlineData(FootballModelKind.DixonColes)]
    public void All_markets_share_valid_joint_mass_and_have_independent_half_time_history(FootballModelKind kind)
    {
        var p = new FootballPredictor(Definition(kind)).Simulate(Input()); Assert.True(p.Warmed && p.HalfWarmed);
        Assert.Equal(1, p.FullTime.Scores.Sum(s => s.Probability)); Assert.Equal(1, p.FullTime.Totals.Sum(s => s.Probability));
        foreach (var target in Enum.GetValues<EvaluationTarget>()) BacktestRules.Validate((target is EvaluationTarget.FirstHalfGoalOccurrence or EvaluationTarget.FirstHalfTotalGoals ? p.HalfTime : p.FullTime).Predict(target), target);
        Assert.Equal(p.FullTime.Totals.Where(t => t.Goals > 2).Sum(t => t.Probability), p.FullTime.Predict(EvaluationTarget.OverUnder25).Probabilities[1]);
        Assert.Equal(p.FullTime.Scores.Sum(s => (s.Home + s.Away) * s.Probability), p.FullTime.Predict(EvaluationTarget.TotalGoals).ExpectedCount);
    }
    [Fact] public void Independent_poisson_matches_analytic_probabilities_with_declared_tail_error()
    {
        var grid = PoissonScores.Build(1, 1, new(), false);
        Assert.InRange(Math.Abs((double)grid.Scores[0].Probability - Math.Exp(-2)), 0, 1e-12);
        Assert.InRange(Math.Abs((double)grid.Predict(EvaluationTarget.BothTeamsScoring).Probabilities[1] - Math.Pow(1 - Math.Exp(-1), 2)), 0, 1e-12);
        Assert.InRange(Math.Abs((double)grid.Predict(EvaluationTarget.OverUnder25).Probabilities[1] - (1 - 5 * Math.Exp(-2))), 0, 1e-12);
        Assert.InRange(Math.Abs((double)grid.Predict(EvaluationTarget.TotalGoals).ExpectedCount!.Value - 2), 0, 1e-11);
        Assert.Equal(grid.Predict(EvaluationTarget.MatchWinner).Probabilities[0], grid.Predict(EvaluationTarget.MatchWinner).Probabilities[2]);
    }
    [Fact] public void Dixon_coles_zero_is_identity_and_nonzero_changes_only_low_score_weights()
    {
        var plain = PoissonScores.Build(1, 1, new(), false); var zero = PoissonScores.Build(1, 1, new(), true);
        Assert.Equal(CanonicalDatasetJson.Serialize(plain), CanonicalDatasetJson.Serialize(zero));
        var corrected = PoissonScores.Build(1, 1, new(Rho: -.1m), true);
        Assert.True(corrected.Scores[0].Probability > plain.Scores[0].Probability);
        Assert.Equal(1, corrected.Scores.Sum(s => s.Probability));
        Assert.Throws<ArgumentException>(() => PoissonScores.Build(2, 2, new(Rho: .5m), true));
    }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(7)]
    public void Invalid_rates_are_rejected_without_clipping(int rate) => Assert.Throws<ArgumentException>(() => PoissonScores.Build(rate, 1, new(), false));
    [Fact] public void Insufficient_truncation_and_invalid_parameters_fail_closed()
    {
        Assert.Throws<InvalidDataException>(() => PoissonScores.Build(5, 5, new(MaximumGoals: 3), false));
        Assert.Throws<ArgumentException>(() => (Definition() with { Elo = new(K: 0) }).Validate());
        Assert.Throws<ArgumentException>(() => (Definition() with { HistoryDays = 365 }).Validate());
        Assert.Throws<ArgumentException>(() => (Definition() with { Goals = new(Rho: -.1m) }).Validate());
    }
    [Fact] public void Missing_history_is_explicit_prior_simulation_and_never_warmed()
    {
        var prior = new FootballPredictor(Definition()).Simulate(Input(0));
        Assert.False(prior.Warmed); Assert.False(prior.HalfWarmed); Assert.Equal(0, prior.HomeMatches);
        Assert.Equal(1.25m, prior.FullTime.HomeRate); Assert.Equal(.55m, prior.HalfTime.HomeRate);
        var noHalf = new FootballPredictor(Definition()).Simulate(Input(3, false)); Assert.True(noHalf.Warmed); Assert.False(noHalf.HalfWarmed);
    }
    [Theory] [InlineData("availability")] [InlineData("recording")] [InlineData("date")] [InlineData("duplicate")] [InlineData("target")]
    public void Future_or_ambiguous_history_never_reaches_model_state(string change)
    {
        var i = Input(1); var m = i.History!.Matches[0];
        m = change switch { "availability" => m with { AvailableAtUtc = T.AddTicks(10) }, "recording" => m with { RecordedAtUtc = T.AddTicks(10) }, "date" => m with { EventDate = DateOnly.FromDateTime(T) }, "target" => m with { EventId = i.EventId }, _ => m };
        var input = i with { History = i.History with { Matches = change == "duplicate" ? [m, m] : [m] } };
        Assert.Throws<InvalidDataException>(() => new FootballPredictor(Definition()).Simulate(input));
    }
    [Fact] public void Replay_correction_and_hyperparameters_change_provenance_hashes()
    {
        var i = Input(); var p = new FootballPredictor(Definition()); var one = p.Simulate(i);
        var corrected = i with { History = i.History! with { Matches = i.History.Matches.Select(m => m with { HomeGoals = 3, ObservationVersion = 2 }).ToArray() } };
        Assert.NotEqual(one.InputHash, p.Simulate(corrected).InputHash);
        Assert.NotEqual(one.DefinitionHash, new FootballPredictor(Definition() with { MinimumMatches = 4 }).Simulate(i).DefinitionHash);
        Assert.Equal(CanonicalDatasetJson.Serialize(one), CanonicalDatasetJson.Serialize(p.Simulate(i)));
    }
    [Fact] public void Model_backtest_preserves_eligibility_and_excludes_unwarmed_prior_predictions()
    {
        var s = BacktestTests.Scenario(); var model = Definition();
        var d = s.Definition with { Version = 2, Predictor = model.Predictor, Model = model };
        var report = new BacktestExecutor(new FootballPredictor(model)).Execute(d, s.Features, s.Evidence);
        Assert.All(report.Samples, x => { Assert.False(x.Eligible); Assert.Contains("model_full_time_warmup_insufficient", x.Reasons); });
        Assert.All(report.Report.Targets, x => Assert.All(x.Metrics, m => { Assert.Equal(0, m.Denominator); Assert.Null(m.Value); }));
        Assert.All(report.Predictions, x => Assert.NotNull(x.Model));
        var legacy = new BacktestExecutor(new SyntheticConstantPredictor()).Execute(s.Definition, s.Features, s.Evidence);
        var other = model with { MinimumMatches = 4 };
        var comparison = ModelComparison.Compare([report, new BacktestExecutor(new FootballPredictor(other)).Execute(d with { Model = other }, s.Features, s.Evidence)]);
        Assert.All(comparison.EquivalentEligibleEvents.Values, Assert.Empty);
        Assert.DoesNotContain("\"Model\"", System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(legacy)));
        Assert.DoesNotContain("\"History\"", System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(BacktestRules.Input(s.Features.Manifest.Rows[0]))));
        Assert.Throws<InvalidDataException>(() => ModelComparison.Compare([report, report with { Definition = d with { EvaluationCutoffUtc = d.EvaluationCutoffUtc.AddTicks(10) } }]));
    }
    [Fact] public void Warmed_historical_models_evaluate_all_markets_and_compare_only_equivalent_events()
    {
        var s = BacktestTests.Scenario(); var row = s.Features.Manifest.Rows[0]; var t = row.Metadata.PredictionCutoffUtc;
        var matches = Enumerable.Range(0, 3).Select(n =>
        {
            var observation = CanonicalDatasetJson.Deserialize<BetStats.Domain.Football.FootballResultObservation>(CanonicalDatasetJson.Serialize(new {
                Id = Id(100 + n), EventId = Id(110 + n), row.Metadata.Target.SourceId, SeasonId = s.Features.Manifest.MetadataManifest.Definition.SeasonId,
                row.Metadata.Target.HomeId, row.Metadata.Target.AwayId, RawId = Id(120 + n), Version = 1,
                EventDate = DateOnly.FromDateTime(t.AddDays(-1)), RetrievedAtUtc = t.AddDays(-1), AvailableAtUtc = t.AddDays(-1), RecordedAtUtc = t.AddDays(-1),
                Value = new BetStats.Domain.Football.FootballResultValue(BetStats.Domain.Football.FootballMatchStatus.Finished, BetStats.Domain.Football.FootballScoreBasis.RegulationTime, new(2, 1), new(1, 0)) }));
            return new FootballResultEvidence(observation, true, [], [Id(130 + n)], [], [], FootballOutcomes.Derive(observation));
        }).ToArray();
        row = row with { FeatureEvidence = row.FeatureEvidence with { Results = matches }, Features = FootballResultFeatures.Compute(row.Metadata.Target, t, matches) };
        row = row with { FeatureHash = CanonicalDatasetJson.Fingerprint(new { SchemaVersion = 3, row.Metadata.Target, row.Metadata.PredictionCutoffUtc, row.FeatureEvidence, row.Features }) };
        var features = s.Features with { Manifest = s.Features.Manifest with { Rows = [row] } }; features = features with { Hash = CanonicalDatasetJson.Fingerprint(features.Manifest) };
        var labels = s.Evidence.Rows[0]; var evidence = s.Evidence with { Rows = [row with { LabelEvidence = labels.LabelEvidence, Labels = labels.Labels }] };
        var reports = Enum.GetValues<FootballModelKind>().Select(kind =>
        {
            var model = Definition(kind); var d = s.Definition with { Version = 2, Predictor = model.Predictor, Model = model, ExpectedDatasetHash = features.Hash };
            return new BacktestExecutor(new FootballPredictor(model)).Execute(d, features, evidence);
        }).ToArray();
        Assert.All(reports, r => { Assert.All(r.Samples, x => Assert.True(x.Eligible, string.Join(',', x.Reasons))); Assert.All(r.Report.Targets, x => { Assert.Equal(1, x.Eligible); Assert.All(x.Metrics, m => { Assert.Equal(1, m.Denominator); Assert.False(m.MinimumSamplesMet); Assert.NotNull(m.Value); }); }); });
        var compared = ModelComparison.Compare(reports); Assert.All(compared.EquivalentEligibleEvents.Values, ids => Assert.Single(ids));
        var unwarmed = Definition() with { MinimumMatches = 4 }; var d2 = reports[1].Definition with { Model = unwarmed };
        var empty = ModelComparison.Compare([reports[1], new BacktestExecutor(new FootballPredictor(unwarmed)).Execute(d2, features, evidence)]);
        Assert.All(empty.Models, m => Assert.All(m.Targets, target => Assert.All(target.Metrics, metric => Assert.Equal(0, metric.Denominator))));
        var lateLabels = evidence with { Rows = [evidence.Rows[0] with { Labels = [] }] };
        Assert.Equal(CanonicalDatasetJson.Serialize(reports[1].Predictions), CanonicalDatasetJson.Serialize(new BacktestExecutor(new FootballPredictor(Definition())).Execute(reports[1].Definition, features, lateLabels).Predictions));
        var folds = WalkForwardReport.From(reports[1].Predictions.Concat(reports[1].Predictions.Select(p => p with { PredictionCutoffUtc = t.AddHours(-1) })).Reverse().ToArray());
        Assert.Equal(t.AddHours(-1), folds.Folds[0].CutoffUtc); Assert.Equal(2, folds.Folds.Count); Assert.All(folds.Folds, f => Assert.Equal(1, f.WarmedEvents));
    }
    [Fact] public void Definition_v1_canonical_bytes_match_original_shape_exactly()
    {
        var d = BacktestTests.Scenario().Definition;
        var old = new { d.Version, d.DatasetId, d.ExpectedDatasetHash, d.Predictor, d.PredictorVersion, d.EvaluationCutoffUtc, d.Evaluations };
        Assert.Equal(CanonicalDatasetJson.Serialize(old), CanonicalDatasetJson.Serialize(d));
        var input = BacktestRules.Input(BacktestTests.Scenario().Features.Manifest.Rows[0]);
        Assert.Equal(CanonicalDatasetJson.Serialize(new { input.Version, input.EventId, input.HomeId, input.AwayId, input.CutoffUtc, input.FeatureHash, input.Features, input.EvidenceIds }), CanonicalDatasetJson.Serialize(input));
    }
}
