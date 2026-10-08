namespace BetStats.Application.Evaluation;

public static class BacktestMetrics
{
    public const int Version = 1;
    public const string Semantics = "decimal-probabilities-exact-sum;equal-weights;class-order-ties;log-natural-rounded-12;binary-brier-two-classes;empty-null;minimum-100";
    public static TargetEvaluation Compute(EvaluationDefinition definition, IReadOnlyList<BacktestSample> samples)
    {
        definition.Validate();
        var rows = samples.Where(s => s.Target == definition.Target).OrderBy(s => s.EventId).ToArray();
        if (rows.Select(s => s.EventId).Distinct().Count() != rows.Length) throw new ArgumentException("One sample per event and target required.");
        var usable = rows.Where(s => s.Eligible).ToArray(); var n = usable.Length;
        foreach (var s in usable)
        {
            BacktestRules.Validate(s.Prediction.Value, s.Target);
            if (s.Prediction.Target != s.Target || s.Prediction.EventId != s.EventId || s.Reasons.Count != 0 || s.ResultObservationId is null || s.ResultVersion is null or < 1 ||
                (BacktestRules.Count(s.Target) ? s.CountLabel is null or < 0 || s.ClassLabel is not null : s.CountLabel is not null || s.ClassLabel is null or < 0 || s.ClassLabel >= BacktestRules.Classes(s.Target).Count))
                throw new ArgumentException("Eligible samples require justified labels of the declared target format.");
        }
        var calibration = BacktestRules.Count(definition.Target) ? [] : BacktestRules.Classes(definition.Target).Select((name, c) => new ClassCalibration(name, Bins(usable, c))).ToArray();
        var metrics = new List<MetricValue>();
        foreach (var m in definition.Metrics.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            decimal? value = null; var infinity = false;
            if (n > 0)
            {
                switch (m.Name)
                {
                    case "accuracy": value = usable.Count(s => ArgMax(s.Prediction.Value.Probabilities) == s.ClassLabel) / (decimal)n; break;
                    case "brier": value = usable.Sum(s => s.Prediction.Value.Probabilities.Select((p, i) => (p - (s.ClassLabel == i ? 1 : 0)) * (p - (s.ClassLabel == i ? 1 : 0))).Sum()) / n; break;
                    case "log_loss":
                        infinity = usable.Any(s => s.Prediction.Value.Probabilities[s.ClassLabel!.Value] == 0);
                        if (!infinity) value = usable.Sum(s => decimal.Round((decimal)-Math.Log((double)s.Prediction.Value.Probabilities[s.ClassLabel!.Value]), 12, MidpointRounding.ToEven)) / n;
                        break;
                    case "mae": value = usable.Sum(s => Math.Abs(s.Prediction.Value.ExpectedCount!.Value - s.CountLabel!.Value)) / n; break;
                    case "calibration_error": value = calibration[1].Bins.Where(b => b.Count > 0).Sum(b => b.Count * Math.Abs(b.MeanProbability!.Value - b.ObservedFrequency!.Value)) / n; break;
                    default: throw new ArgumentException("Unsupported metric version.");
                }
            }
            metrics.Add(new(m.Name, m.Version, n, value, infinity, n >= m.MinimumSamples, m.Name == "calibration_error" ? calibration[1].Bins : []));
        }
        var reasons = rows.Where(s => !s.Eligible).SelectMany(s => s.Reasons.Distinct()).GroupBy(r => r).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        return new(definition.Target, rows.Length, n, rows.Length - n, metrics, reasons, calibration);
    }
    private static int ArgMax(IReadOnlyList<decimal> ps) => Enumerable.Range(0, ps.Count).OrderByDescending(i => ps[i]).ThenBy(i => i).First();
    private static IReadOnlyList<CalibrationBin> Bins(IReadOnlyList<BacktestSample> samples, int c) => Enumerable.Range(0, 10).Select(i =>
    {
        var rows = samples.Where(s => Math.Min(9, (int)(s.Prediction.Value.Probabilities[c] * 10)) == i).ToArray();
        return new CalibrationBin(i, rows.Length, rows.Length == 0 ? null : rows.Average(s => s.Prediction.Value.Probabilities[c]),
            rows.Length == 0 ? null : rows.Count(s => s.ClassLabel == c) / (decimal)rows.Length);
    }).ToArray();
}
