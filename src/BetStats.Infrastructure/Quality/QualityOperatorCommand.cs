using System.Globalization;
using System.Text.Json;
using BetStats.Application.Quality;
using BetStats.Application.Ingestion;
using BetStats.Domain.Identity;
using BetStats.Domain.Governance;
using BetStats.Domain.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Quality;

// Explicit development-only Worker entry; no HTTP routes or authentication claim.
public static class QualityOperatorCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, CancellationToken token = default)
    {
        string Text(string name) => config["Quality:" + name] ?? throw new ArgumentException("Required Quality:" + name);
        Guid Id(string name) => Guid.Parse(Text(name));
        DateTime Time(string name) => DateTime.Parse(Text(name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        object result;
        switch (Text("Action"))
        {
            case "list": result = await services.GetRequiredService<IIdentityReview>().ListUnresolvedAsync(Id("SourceId"), token: token); break;
            case "inspect": result = await services.GetRequiredService<IIdentityReview>().InspectAsync(Id("IdentityId"), token); break;
            case "candidates": result = await services.GetRequiredService<IIdentityReview>().CandidatesAsync(Id("IdentityId"), token: token); break;
            case "review":
                var action = Enum.Parse<ReviewAction>(Text("Decision"), ignoreCase: false);
                var review = await services.GetRequiredService<IIdentityReview>().DecideAsync(new(Id("IdentityId"), Id("SourceId"), action,
                    action == ReviewAction.Approve ? new(Enum.Parse<CanonicalEntityKind>(Text("TargetKind")), Id("TargetId")) : null,
                    int.Parse(Text("ExpectedVersion"), CultureInfo.InvariantCulture), Text("OperatorId"), Text("Reason")), token);
                Console.WriteLine(JsonSerializer.Serialize(review)); return review.Result == "accepted" ? 0 : 1;
            case "reconcile":
                var reconciliation = await services.GetRequiredService<IDataReconciliation>().RunAsync([new(Id("RawId"), new(Text("Competition"), Text("Season")))], Text("OperatorId"), Text("Reason"), token);
                Console.WriteLine(JsonSerializer.Serialize(reconciliation)); return reconciliation.Result == "Completed" && reconciliation.Items.All(i => i.Outcome is not (ReconciliationOutcome.Failed or ReconciliationOutcome.Conflict)) ? 0 : 1;
            case "interrupt":
                await services.GetRequiredService<IDataReconciliation>().MarkInterruptedAsync(Id("ExecutionId"), Text("OperatorId"), Text("Reason"), token);
                result = new { Result = "InterruptionRecorded" }; break;
            case "report": result = await services.GetRequiredService<IQualityReports>().ReadAsync(Id("ExecutionId"), token: token); break;
            case "eligibility":
                var mode = Enum.Parse<DatasetMode>(Text("Mode"));
                result = await services.GetRequiredService<IAnalyticalQualityGate>().EvaluateAsync(new(Id("ObservationId"), Time("AsOfUtc"),
                    Enum.Parse<DataPurpose>(Text("Purpose")), new(), mode,
                    mode == DatasetMode.RetrospectiveReconstruction ? Time("ReconstructionAtUtc") : null), token); break;
            default: throw new ArgumentException("Unsupported explicit quality action.");
        }
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); return 0;
    }
}
