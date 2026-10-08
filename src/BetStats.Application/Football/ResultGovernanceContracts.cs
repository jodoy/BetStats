using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Domain.Coverage;
using BetStats.Domain.Football;

namespace BetStats.Application.Football;

public sealed record ResultInventoryClaim(int Version, string Contract, ResultCoverageScope Scope, ResultCoverageStatus Claim,
    IReadOnlyList<string> ExpectedEventReferences, IReadOnlyList<Guid> ResultObservationIds, DateTime? PublishedUtc = null);
public sealed record ResultInventorySubmission(Guid RawId, ResultInventoryClaim Claim, DateTime AvailableUtc, DateTime ValidUntilUtc,
    string OperatorId, string Reason, bool Approved, Guid? CorrectsId = null, DateTime? PublishedUtc = null);
public sealed record ResultReviewRequest(Guid EvidenceId, int ExpectedSequence, bool Approve, string OperatorId, string Reason, bool Approved);
public sealed record ResultCoverageQuery(ResultCoverageScope Scope, FootballResultQuery Results);
public sealed record ResultCoverageItem(ResultInventoryEvidence Evidence, ResultInventoryReview? Review, ResultCoverageStatus Status,
    ResultInventoryClaim? Inventory, IReadOnlyList<string> Reasons);
public sealed record ResultCoverageReport(int Version, ResultCoverageQuery Query, ResultCoverageStatus Status,
    IReadOnlyList<ResultCoverageItem> Items, IReadOnlyList<Guid> ObservedResultIds, IReadOnlyList<string> Reasons);
public sealed record EventEndSourceClaim(int Version, Guid OriginalRawId, string ProviderEventReference,
    FootballTimeContext Context, Guid ResultObservationId, EventTimeValue Value, DateTime? PublishedUtc = null);
public sealed record EventEndSubmission(Guid RawId, EventEndSourceClaim Claim, DateTime AvailableUtc,
    string OperatorId, string Reason, bool Approved, Guid? CorrectsId = null, DateTime? PublishedUtc = null);
public sealed record EventEndClaimResult(EventEndEvidence Evidence, Guid EventId, Guid IdentityDecisionId,
    EventTimeResolution Resolution, bool Eligible, IReadOnlyList<string> Reasons);
public sealed record ResultGovernanceRow(Guid EventId, ResultCoverageReport FeatureCoverage, ResultCoverageReport LabelCoverage,
    IReadOnlyList<EventEndClaimResult> Ends);
public sealed record ResultDatasetGovernance(int Version, IReadOnlyList<ResultGovernanceRow> Rows);
public interface IResultGovernance
{
    Task<ResultInventoryEvidence> RecordAsync(ResultInventorySubmission request, CancellationToken token = default);
    Task<ResultInventoryReview> ReviewAsync(ResultReviewRequest request, CancellationToken token = default);
    Task<ResultCoverageReport> ReportAsync(ResultCoverageQuery query, CancellationToken token = default);
    Task<EventEndEvidence> RecordEndAsync(EventEndSubmission request, CancellationToken token = default);
    Task<IReadOnlyList<EventEndClaimResult>> EndsAsync(FootballResultQuery query, CancellationToken token = default);
    Task<ResultDatasetGovernance> FreezeAsync(FootballResultManifest manifest, CancellationToken token = default);
    Task EnsureCurrentAsync(ResultDatasetGovernance governance, FootballResultManifest manifest, CancellationToken token = default);
    Task<bool> VerifyFrozenAsync(ResultDatasetGovernance governance, CancellationToken token = default);
}
public static class ResultCoverageRules
{
    public const string OwnedContract = "project-owned-result-inventory-v1";
    public static ResultCoverageStatus Classify(ResultCoverageScope scope, IReadOnlyList<ResultCoverageItem> items, bool observedConflict = false)
    {
        if (observedConflict || items.Any(i => i.Status == ResultCoverageStatus.Conflict)) return ResultCoverageStatus.Conflict;
        var active = items.Where(i => i.Status is ResultCoverageStatus.Complete or ResultCoverageStatus.Empty).ToArray();
        for (var i = 0; i < active.Length; i++) for (var j = i + 1; j < active.Length; j++)
        {
            var overlap = CoverageRules.Intersection(active[i].Evidence.Scope.Interval, active[j].Evidence.Scope.Interval);
            // Differing strong claims never choose a winner using a recording timestamp.
            if (overlap is not null && active[i].Status != active[j].Status) return ResultCoverageStatus.Conflict;
        }
        if (CoverageRules.Gaps(scope.Interval, active.Select(i => i.Evidence.Scope.Interval)).Count == 0)
            return active.All(i => i.Status == ResultCoverageStatus.Empty) ? ResultCoverageStatus.Empty : ResultCoverageStatus.Complete;
        if (active.Length > 0 || items.Any(i => i.Status == ResultCoverageStatus.Partial)) return ResultCoverageStatus.Partial;
        return items.Count > 0 && items.All(i => i.Status == ResultCoverageStatus.Expired) ? ResultCoverageStatus.Expired : ResultCoverageStatus.Unknown;
    }
}
