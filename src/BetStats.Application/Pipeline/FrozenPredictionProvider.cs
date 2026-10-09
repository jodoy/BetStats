using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Models;

namespace BetStats.Application.Pipeline;

// Replays immutable values and model provenance. No model is run during evaluation.
public sealed class FrozenPredictionProvider(BacktestManifest frozen) : IHistoricalFootballPredictionProvider
{
    public string Name => frozen.Definition.Predictor;
    public int Version => frozen.Definition.PredictorVersion;
    public FootballModelDefinition Definition => frozen.Definition.Model ?? throw new InvalidDataException("Frozen model required.");
    private HistoricalPrediction Read(PredictionInput input, EvaluationTarget target)
    {
        var p = frozen.Predictions.Single(p => p.EventId == input.EventId && p.Target == target);
        if (p.InputHash != CanonicalDatasetJson.Fingerprint(input) || p.PredictionCutoffUtc != input.CutoffUtc || p.FeatureHash != input.FeatureHash)
            throw new InvalidDataException("Frozen prediction input mismatch.");
        return p;
    }
    public PredictionValue Predict(PredictionInput input, EvaluationTarget target) => Read(input, target).Value;
    public ModelProvenance Simulate(PredictionInput input)
    {
        foreach (var target in frozen.Definition.Evaluations.Select(e => e.Target)) _ = Read(input, target);
        return frozen.ModelForecasts!.Single(f => f.EventId == input.EventId && f.CutoffUtc == input.CutoffUtc).Provenance;
    }
}
