using System.Text;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Football;

public static class ResultOperatorCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, bool development, CancellationToken token = default)
    {
        if (!development) throw new InvalidOperationException("Result operator commands require Development.");
        string Text(string key) => config["Results:" + key] ?? throw new ArgumentException("Required Results:" + key);
        T Json<T>(string key) => CanonicalDatasetJson.Deserialize<T>(Encoding.UTF8.GetBytes(Text(key)));
        var actor = Text("OperatorId"); var reason = Text("Reason"); QualityPersistence.Operator(actor, reason);
        var approved = config["Results:Approve"] == "true";
        var governance = services.GetRequiredService<IResultGovernance>(); var operations = services.GetRequiredService<IResultDatasetOperations>();
        object result; var success = true;
        switch (Text("Action"))
        {
            case "capture-evidence":
                if (!approved) throw new ArgumentException("Explicit RAW evidence capture approval required.");
                var persistence = services.GetRequiredService<IFootballIngestionPersistence>();
                var sourceId = Guid.Parse(Text("SourceId"));
                await persistence.EnsureCaptureAllowedAsync(sourceId, token);
                var file = new FileInfo(Text("PayloadPath"));
                if (!file.Exists || file.Length is < 1 or > 1048576) throw new ArgumentException("Bounded local evidence payload required.");
                var bytes = await File.ReadAllBytesAsync(file.FullName, token);
                var storage = services.GetRequiredService<IRawPayloadStore>();
                var attempt = await persistence.BeginAsync(Guid.NewGuid(), sourceId, token);
                try
                {
                    var staged = await storage.StageAsync(bytes, token);
                    var now = await QualityPersistence.Now(services.GetRequiredService<BetStatsDbContext>(), token);
                    var captured = await persistence.CaptureAsync(attempt, new(bytes, "application/json", now), staged, Json<FootballImportScope>("ScopeJson"), token);
                    await storage.FinalizeAsync(staged, token);
                    await persistence.CompleteAsync(attempt with { Outcome = ImportOutcome.Succeeded, RetrievedPayloads = 1 }, token);
                    result = new { captured.Id, captured.RecordedAtUtc, OperatorId = actor, Reason = reason };
                }
                catch { await persistence.CompleteAsync(attempt with { Outcome = ImportOutcome.Failed, ErrorCode = "evidence_capture_failure" }, CancellationToken.None); throw; }
                break;
            case "coverage": result = await governance.ReportAsync(Json<ResultCoverageQuery>("QueryJson"), token); break;
            case "record-inventory": result = await governance.RecordAsync(Json<ResultInventorySubmission>("SubmissionJson") with { OperatorId = actor, Reason = reason, Approved = approved }, token); break;
            case "review-inventory":
                var review = await governance.ReviewAsync(Json<ResultReviewRequest>("SubmissionJson") with { OperatorId = actor, Reason = reason, Approved = approved }, token);
                result = review; success = review.Approved; break;
            case "record-end": result = await governance.RecordEndAsync(Json<EventEndSubmission>("SubmissionJson") with { OperatorId = actor, Reason = reason, Approved = approved }, token); break;
            case "ends": result = await governance.EndsAsync(Json<FootballResultQuery>("QueryJson"), token); break;
            case "build-v3":
                var built = await operations.BuildAsync(new(Guid.Parse(Text("OperationId")), Json<FootballResultDatasetRequest>("RequestJson"), actor, reason, approved), token);
                result = built; success = built.Status == ResultOperationStatus.Succeeded; break;
            case "recover-v3":
                var recovered = await operations.RecoverAsync(new(Guid.Parse(Text("OperationId")), Text("ExpectedFingerprint"), actor, reason, approved), token);
                result = recovered; success = recovered.Status == ResultOperationStatus.Succeeded; break;
            case "operation": result = await operations.InspectAsync(Guid.Parse(Text("OperationId")), token); break;
            case "inspect-v3": result = await services.GetRequiredService<IFootballResultDatasets>().InspectAsync(Guid.Parse(Text("SnapshotId")), token); break;
            case "verify-v3": case "verify-v3-deep":
                var deep = Text("Action") == "verify-v3-deep";
                var verification = await operations.VerifyAsync(Guid.Parse(Text("SnapshotId")), deep, token);
                result = verification; success = verification.Integrity && verification.FeaturesReproducible && verification.CurrentlyAuthorized && (!deep || verification.RawAvailable == true && verification.RawHashVerified == true); break;
            case "demo-clock":
                if (!approved) throw new ArgumentException("Explicit fixture approval required.");
                var fixture = SyntheticResultScenario.Create(services.GetRequiredService<TimeProvider>());
                var source = await SyntheticFootballDemo.PrepareAsync(services.GetRequiredService<BetStatsDbContext>(), true, Text("SourceCode"), token, fixtureScope: fixture.Scope);
                var ingestion = services.GetRequiredService<FootballIngestion>(); var reports = new List<ImportReport>();
                foreach (var csv in new[] { fixture.Csv, fixture.Csv, fixture.Correction, fixture.Correction })
                    reports.Add(await ingestion.RunAsync(new FootballDataFixtureAdapter(source, SyntheticFootballDemo.Bytes(csv), services.GetRequiredService<TimeProvider>()), fixture.Scope, services.GetRequiredService<RequestBudget>(), token));
                result = new { Fixture = fixture, SourceId = source, Imports = reports }; success = reports.All(r => r.Outcome is ImportOutcome.Succeeded or ImportOutcome.Reused); break;
            default: throw new ArgumentException("Unsupported explicit result action.");
        }
        Console.WriteLine(Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new { Action = Text("Action"), OperatorId = actor, Reason = reason, Result = result })));
        return success ? 0 : 1;
    }
}
