using System.Text;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Models;
using BetStats.Infrastructure.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Evaluation;

public static class ModelOperatorCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, bool development, CancellationToken token = default)
    {
        if (!development) throw new InvalidOperationException("Models require explicit Development execution.");
        string Text(string key) => config["Model:" + key] ?? throw new ArgumentException("Required Model:" + key);
        var actor = Text("OperatorId"); var reason = Text("Reason"); QualityPersistence.Operator(actor, reason);
        var action = Text("Action"); object result;
        if (action is "inspect" or "simulate")
        {
            var model = CanonicalDatasetJson.Deserialize<FootballModelDefinition>(Encoding.UTF8.GetBytes(Text("DefinitionJson"))); model.Validate();
            result = action == "inspect" ? new { Definition = model, Hash = CanonicalDatasetJson.Fingerprint(model), FeatureRequirements = "reviewed historical canonical results; 30 UTC days; both clocks <= cutoff; separate half-time warm-up" } :
                new { ExecutionKind = "unverified-mathematical-simulation-not-operational-history", EligibleOperationalSamples = 0,
                    Prediction = new FootballPredictor(model).Simulate(CanonicalDatasetJson.Deserialize<PredictionInput>(Encoding.UTF8.GetBytes(Text("InputJson")))) };
        }
        else if (action == "compare")
        {
            var ids = Text("SnapshotIds").Split(',').Select(Guid.Parse).ToArray();
            if (ids.Length is < 2 or > 10) throw new ArgumentException("Two to ten verified snapshots required.");
            var ops = services.GetRequiredService<IHistoricalBacktests>(); var reports = new List<BacktestManifest>();
            foreach (var id in ids)
            {
                var v = await ops.VerifyAsync(id, true, token);
                if (!v.Integrity || !v.Reproducible || !v.CurrentlyAuthorized || v.RawAvailable != true || v.RawHashVerified != true) throw new InvalidDataException("Comparison requires current permission and deep verification.");
                reports.Add((await ops.InspectAsync(id, token)).Manifest);
            }
            result = ModelComparison.Compare(reports);
        }
        else
        {
            if (action is not ("plan" or "backtest" or "verify" or "verify-deep" or "recover" or "operation" or "report")) throw new ArgumentException("Unsupported model action.");
            if (action is "plan" or "backtest")
            {
                var d = CanonicalDatasetJson.Deserialize<BacktestDefinition>(Encoding.UTF8.GetBytes(Text("DefinitionJson"))); d.Validate();
                if (d.Version != 2 || d.Model is null) throw new ArgumentException("Model backtest definition v2 required.");
            }
            var mapped = new ConfigurationBuilder().AddInMemoryCollection(config.AsEnumerable().Where(p => p.Key.StartsWith("Model:", StringComparison.Ordinal)).Select(p => new KeyValuePair<string, string?>("Backtest:" + p.Key[6..], p.Value))).Build();
            mapped["Backtest:Action"] = action switch { "backtest" => "run", "report" => "inspect", _ => action };
            return await BacktestOperatorCommand.RunAsync(services, mapped, true, token);
        }
        Console.WriteLine(Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new { Action = action, OperatorId = actor, Reason = reason, Result = result })));
        return 0;
    }
}
