using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Quality;

namespace BetStats.Application.Quality;

public enum ReviewAction { Approve, Reject, Ambiguous }
public sealed record IdentityReviewCommand(Guid ProviderIdentityId, Guid DataSourceId, ReviewAction Action, CanonicalReference? Target,
    int ExpectedVersion, string OperatorId, string Reason);
public sealed record ReviewResult(string Result, Guid? DecisionId, int Version, Guid AuditId);
public sealed record IdentityReviewItem(ProviderIdentity Identity, IdentityResolution? Decision, IReadOnlyList<Guid> RawPayloadIds);
public sealed record CanonicalCandidate(CanonicalEntityKind Kind, Guid Id, string Label);
public interface IIdentityReview
{
    Task<IReadOnlyList<IdentityReviewItem>> ListUnresolvedAsync(Guid sourceId, int limit = 100, CancellationToken token = default);
    Task<IdentityReviewItem> InspectAsync(Guid identityId, CancellationToken token = default);
    Task<IReadOnlyList<CanonicalCandidate>> CandidatesAsync(Guid identityId, int limit = 50, CancellationToken token = default);
    Task<ReviewResult> DecideAsync(IdentityReviewCommand command, CancellationToken token = default);
}
public sealed record RawReconciliationRequest(Guid RawPayloadId, FootballImportScope Scope);
public enum ReconciliationOutcome { AlreadyProcessed, NewlyResolved, StillUnresolved, Conflict, Rejected, Failed }
public sealed record ReconciliationItem(Guid RawPayloadId, int Row, ReconciliationOutcome Outcome, string ReasonCode);
public sealed record ReconciliationResult(Guid ExecutionId, string Result, IReadOnlyList<ReconciliationItem> Items);
public interface IDataReconciliation
{
    Task<ReconciliationResult> RunAsync(IReadOnlyList<RawReconciliationRequest> requests, string operatorId, string reason, CancellationToken token = default);
    Task MarkInterruptedAsync(Guid executionId, string operatorId, string reason, CancellationToken token = default);
}
public sealed record EligibilityQuery(Guid ObservationId, DateTime AsOfUtc, DataPurpose Purpose, UsageContext Context,
    DatasetMode Mode = DatasetMode.HistoricalAsKnown, DateTime? ReconstructionAtUtc = null);
public interface IAnalyticalQualityGate { Task<QualityGateResult> EvaluateAsync(EligibilityQuery query, CancellationToken token = default); }
public sealed record QualityReport(Guid ExecutionId, int Total, int Valid, int Invalid, int Duplicates, int Unresolved, int Conflicts,
    int Accepted, int Rejected, int Eligible, double ValidationPassRate, double IdentityResolutionRate, double ConflictRate,
    double AnalyticalEligibilityRate, IReadOnlyList<string> RuleVersions, DateTime? AssessedAtUtc, IReadOnlyList<Guid> PolicyIds, int PayloadFailures = 0);
public interface IQualityReports { Task<QualityReport> ReadAsync(Guid executionId, int maximumRecords = 5000, CancellationToken token = default); }
