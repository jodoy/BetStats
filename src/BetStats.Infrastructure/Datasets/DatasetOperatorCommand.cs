using System.Globalization;
using System.Text.Json;
using System.Text;
using BetStats.Application.Datasets;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Datasets;

public static class DatasetOperatorCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, CancellationToken token = default)
    {
        string Text(string key) => config["Dataset:" + key] ?? throw new ArgumentException("Required Dataset:" + key);
        Guid Id(string key) => Guid.Parse(Text(key));
        var datasets = services.GetRequiredService<IDatasets>(); object result; var success = true;
        switch (Text("Action"))
        {
            case "build":
                var definitionInput = CanonicalDatasetJson.Deserialize<DatasetDefinition>(Encoding.UTF8.GetBytes(Text("DefinitionJson")));
                var frozen = await datasets.BuildAsync(new(definitionInput, Text("OperatorId"), Text("Reason")), token);
                result = frozen; success = frozen.Status == DatasetBuildStatus.Succeeded; break;
            case "build-synthetic":
                if (Text("ApproveSynthetic") != "true") throw new ArgumentException("Explicit synthetic approval required.");
                var (_, definition) = await SyntheticDatasetDemo.PrepareAsync(services.GetRequiredService<BetStatsDbContext>(),
                    services.GetRequiredService<FootballIngestion>(), services.GetRequiredService<RequestBudget>(), services.GetRequiredService<TimeProvider>(),
                    true, Text("SourceCode"), DateOnly.ParseExact(Text("TargetDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture), token);
                var built = await datasets.BuildAsync(new(definition, Text("OperatorId"), Text("Reason")), token);
                result = built; success = built.Status == DatasetBuildStatus.Succeeded; break;
            case "inspect": result = await datasets.InspectAsync(Id("SnapshotId"), token); break;
            case "verify":
                var verification = await datasets.VerifyAsync(Id("SnapshotId"), token); result = verification;
                success = verification.ArtifactIntegrity && verification.EvidenceComplete && verification.CurrentlyAuthorized && verification.FeaturesReproducible; break;
            case "verify-deep":
                var deep = await datasets.VerifyDeepAsync(Id("SnapshotId"), token); result = deep;
                success = deep.ArtifactIntegrity && deep.FrozenMetadataComplete && deep.CurrentUseAuthorized && deep.FeaturesReproducible && deep.RawAvailable == true && deep.RawHashVerified == true; break;
            case "compare": result = await datasets.CompareAsync(Id("LeftId"), Id("RightId"), int.Parse(Text("Offset"), CultureInfo.InvariantCulture), int.Parse(Text("Limit"), CultureInfo.InvariantCulture), token); break;
            case "interrupt": await datasets.MarkInterruptedAsync(Id("AttemptId"), Text("OperatorId"), Text("Reason"), token); result = new { Result = "InterruptionRecorded" }; break;
            default: throw new ArgumentException("Unsupported explicit dataset action.");
        }
        Console.WriteLine(JsonSerializer.Serialize(result)); return success ? 0 : 1;
    }
}
