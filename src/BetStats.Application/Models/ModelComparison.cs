using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;

namespace BetStats.Application.Models;

public sealed record WalkForwardFold(DateTime CutoffUtc, IReadOnlyList<Guid> Events, int WarmedEvents, int HalfWarmedEvents);
public sealed record WalkForwardReport(int Version, int HistoryDays, string StateRule, IReadOnlyList<WalkForwardFold> Folds)
{
    public static WalkForwardReport From(IReadOnlyList<HistoricalPrediction> predictions) => new(1, 30,
        "independent-as-known-replay; prior-calendar-days-only; no-fitted-parameters; no-random-split",
        predictions.GroupBy(p => p.PredictionCutoffUtc).OrderBy(g => g.Key).Select(g => new WalkForwardFold(g.Key,
            g.Select(p => p.EventId).Distinct().Order().ToArray(), g.Where(p => p.Model?.Warmed == true).Select(p => p.EventId).Distinct().Count(),
            g.Where(p => p.Model?.HalfWarmed == true).Select(p => p.EventId).Distinct().Count())).ToArray());
}

public sealed record EquivalentModelMetrics(string ModelHash, IReadOnlyList<TargetEvaluation> Targets);
public sealed record ModelComparisonReport(int Version, string DatasetHash, int RequestedModels,
    IReadOnlyDictionary<EvaluationTarget, IReadOnlyList<Guid>> EquivalentEligibleEvents, IReadOnlyList<EquivalentModelMetrics> Models);
public static class ModelComparison
{
    public static ModelComparisonReport Compare(IReadOnlyList<BacktestManifest> manifests)
    {
        if (manifests.Count is < 2 or > 10) throw new ArgumentException("Compare two to ten verified reports.");
        var first = manifests[0];
        foreach (var m in manifests)
        {
            m.Definition.Validate();
            if (m.ModelForecasts is null || m.ModelForecasts.GroupBy(f => (f.EventId, f.CutoffUtc)).Any(g => g.Count() != 1)) throw new InvalidDataException("Unique frozen model forecasts required.");
            if (m.Samples.GroupBy(s => (s.EventId, s.Target)).Any(g => g.Count() != 1) ||
                m.Predictions.Any(p => p.Model is null || p.Model.DefinitionHash != CanonicalDatasetJson.Fingerprint(m.Definition.Model!) ||
                    CanonicalDatasetJson.Fingerprint(p.Model.Definition) != p.Model.DefinitionHash || p.InputHash != p.Model.InputHash))
                throw new InvalidDataException("Comparison model provenance or sample uniqueness failed.");
            foreach (var p in m.Predictions)
            {
                var forecast = m.ModelForecasts.SingleOrDefault(f => f.EventId == p.EventId && f.CutoffUtc == p.PredictionCutoffUtc);
                if (forecast is null || CanonicalDatasetJson.Fingerprint(PredictionModelProvenance.From(forecast.Provenance)) != CanonicalDatasetJson.Fingerprint(p.Model!)) throw new InvalidDataException("Frozen score distributions are not bound to predictions.");
            }
        }
        if (manifests.Any(m => m.Definition.Model is null || m.Definition.ExpectedDatasetHash != first.Definition.ExpectedDatasetHash || m.Definition.DatasetId != first.Definition.DatasetId ||
            m.Definition.EvaluationCutoffUtc != first.Definition.EvaluationCutoffUtc || CanonicalDatasetJson.Fingerprint(m.Definition.Evaluations) != CanonicalDatasetJson.Fingerprint(first.Definition.Evaluations) ||
            CanonicalDatasetJson.Fingerprint(m.EvaluationEvidence) != CanonicalDatasetJson.Fingerprint(first.EvaluationEvidence)))
            throw new InvalidDataException("Comparison requires equivalent dataset, label evidence, cutoffs and metric contracts.");
        var events = first.Definition.Evaluations.ToDictionary(e => e.Target, e => (IReadOnlyList<Guid>)manifests
            .Select(m => m.Samples.Where(s => s.Target == e.Target && s.Eligible).Select(s => s.EventId).ToHashSet()).Aggregate((left, right) => { left.IntersectWith(right); return left; }).Order().ToArray());
        var models = manifests.Select(m => new EquivalentModelMetrics(CanonicalDatasetJson.Fingerprint(m.Definition.Model!),
            m.Definition.Evaluations.OrderBy(e => e.Target).Select(e => BacktestMetrics.Compute(e,
                m.Samples.Where(s => s.Target == e.Target && events[e.Target].Contains(s.EventId)).ToArray())).ToArray())).ToArray();
        return new(1, first.Definition.ExpectedDatasetHash, manifests.Count, events, models);
    }
}
