using System.Globalization;
using System.Text;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.Infrastructure.Coverage;

// Only the Worker composition root may enable this local development entry.
public static class CoverageOperatorCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, bool development = false, CancellationToken token = default)
    {
        if (!development) throw new InvalidOperationException("Coverage commands require Development environment.");
        string Text(string key) => config["Coverage:" + key] ?? throw new ArgumentException("Required Coverage:" + key);
        T Json<T>(string key) => CanonicalDatasetJson.Deserialize<T>(Encoding.UTF8.GetBytes(Text(key)));
        var actor = Text("OperatorId"); var reason = Text("Reason"); QualityPersistence.Operator(actor, reason);
        var action = Text("Action"); object result; var success = true;
        if (action == "evaluation-contracts") result = new { Targets = Enum.GetNames<EvaluationTarget>(), Metrics = EvaluationContracts.Metrics,
            TemporalRule = "features available and recorded by prediction; labels afterward and by evaluation; explicit correction version" };
        else
        {
            var coverage = services.GetRequiredService<IHistoricalCoverage>();
            switch (action)
            {
                case "record": result = await coverage.RecordAsync(Json<CoverageSubmission>("SubmissionJson") with { OperatorId = actor, Reason = reason }, token); break;
                case "record-time": result = await coverage.RecordTimeAsync(Json<EventTimeSubmission>("SubmissionJson") with { OperatorId = actor, Reason = reason }, token); break;
                case "inspect": result = await coverage.InspectAsync(Guid.Parse(Text("EvidenceId")), token); break;
                case "review":
                    var review = await coverage.ReviewAsync(new(Guid.Parse(Text("EvidenceId")), int.Parse(Text("ExpectedSequence"), CultureInfo.InvariantCulture),
                        Enum.Parse<CoverageReviewStatus>(Text("Decision")), Text("BasisReference"), actor, reason), token);
                    result = review; success = review.Status == CoverageReviewStatus.Approved; break;
                case "report": result = await coverage.ReportAsync(Json<CoverageQuery>("QueryJson"), token); break;
                case "feature-gate":
                    var queries = Json<CoverageQuery[]>("QueriesJson"); if (queries.Length is < 1 or > 10) throw new ArgumentException("Bounded explicit queries required.");
                    var reports = new List<CoverageReport>(); foreach (var query in queries) reports.Add(await coverage.ReportAsync(query, token));
                    var decision = CoverageRules.Gate(Json<FeatureCoverageRequirement>("RequirementJson"), reports); result = decision;
                    success = decision.Outcome is FeatureCoverageOutcome.Eligible or FeatureCoverageOutcome.EligibleWithPartialCoverage; break;
                case "time":
                    var queryTime = Json<CoverageQuery>("QueryJson");
                    result = await coverage.TimesAsync(Guid.Parse(Text("IdentityId")), queryTime.AsOfUtc, queryTime.Mode, queryTime.ReconstructionUtc, queryTime.Purpose, queryTime.Context, token); break;
                default: throw new ArgumentException("Unsupported explicit coverage action.");
            }
        }
        Console.WriteLine(Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new { OperatorId = actor, Reason = reason, Action = action, Result = result })));
        return success ? 0 : 1;
    }
}
