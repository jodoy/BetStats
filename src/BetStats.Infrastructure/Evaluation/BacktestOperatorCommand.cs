using System.Text;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Infrastructure.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Evaluation;

public static class BacktestOperatorCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, bool development, CancellationToken token = default)
    {
        if (!development) throw new InvalidOperationException("Backtests require explicit Development execution.");
        string Text(string key) => config["Backtest:" + key] ?? throw new ArgumentException("Required Backtest:" + key);
        var actor = Text("OperatorId"); var reason = Text("Reason"); QualityPersistence.Operator(actor, reason);
        var action = Text("Action"); var approved = config["Backtest:Approve"] == "true";
        var backtests = services.GetRequiredService<IHistoricalBacktests>(); object result; var success = true;
        BacktestDefinition Definition() => CanonicalDatasetJson.Deserialize<BacktestDefinition>(Encoding.UTF8.GetBytes(Text("DefinitionJson")));
        switch (action)
        {
            case "plan": result = await backtests.PlanAsync(Definition(), token); break;
            case "run":
                var run = await backtests.RunAsync(new(Guid.Parse(Text("OperationId")), Definition(), actor, reason, approved), token);
                result = run; success = run.Status == ResultOperationStatus.Succeeded; break;
            case "recover":
                var recovered = await backtests.RecoverAsync(new(Guid.Parse(Text("OperationId")), Text("ExpectedFingerprint"), actor, reason, approved), token);
                result = recovered; success = recovered.Status == ResultOperationStatus.Succeeded; break;
            case "operation": result = await backtests.OperationAsync(Guid.Parse(Text("OperationId")), token); break;
            case "inspect": result = await backtests.InspectAsync(Guid.Parse(Text("SnapshotId")), token); break;
            case "verify": case "verify-deep":
                var deep = action == "verify-deep"; var verification = await backtests.VerifyAsync(Guid.Parse(Text("SnapshotId")), deep, token);
                result = verification; success = verification.Integrity && verification.Reproducible && verification.CurrentlyAuthorized && (!deep || verification.RawAvailable == true && verification.RawHashVerified == true); break;
            default: throw new ArgumentException("Unsupported explicit backtest action.");
        }
        Console.WriteLine(Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new { Action = action, OperatorId = actor, Reason = reason, Result = result })));
        return success ? 0 : 1;
    }
}
