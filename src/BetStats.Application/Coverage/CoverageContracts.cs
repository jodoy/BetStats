using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;

namespace BetStats.Application.Coverage;

public sealed record CoverageSubmission(CoverageScope Scope, CoverageStatus Claim, CoverageBasis Basis, string EvidenceReference,
    Guid RawId, int Version, DateTime? PublicationUtc, DateTime AvailableUtc, DateTime ValidUntilUtc, string OperatorId, string Reason);
public sealed record CoverageReviewRequest(Guid EvidenceId, int ExpectedSequence, CoverageReviewStatus Status, string BasisReference, string OperatorId, string Reason);
public sealed record CoverageQuery(CoverageScope Scope, DateTime AsOfUtc, DatasetMode Mode, DateTime? ReconstructionUtc, DataPurpose Purpose, UsageContext Context);
public sealed record CoverageItem(CoverageEvidence Evidence, CoverageReview? Review, CoverageStatus Status, IReadOnlyList<Guid> IdentityDecisionIds,
    IReadOnlyList<Guid> QualityIds, IReadOnlyList<string> Reasons);
public sealed record CoverageReport(CoverageQuery Query, CoverageStatus Status, bool Authorized, IReadOnlyList<CoverageItem> Items,
    IReadOnlyList<CoverageInterval> UnknownIntervals, IReadOnlyList<CoverageInterval> PartialIntervals, IReadOnlyList<CoverageInterval> ConflictingIntervals,
    IReadOnlyList<ObservationType> MissingObservationTypes, IReadOnlyList<string> Reasons);
public sealed record CoverageInventory(string Contract, CoverageScope Scope, CoverageStatus Claim, IReadOnlyList<Guid> ObservationIds);
public enum FeatureCoverageOutcome { Eligible, EligibleWithPartialCoverage, InsufficientCoverage, UnknownCoverage, ConflictingCoverage, ExpiredCoverage, Unauthorized }
public sealed record FeatureCoverageRequirement(string FeatureName, int Version, IReadOnlyList<ObservationType> ObservationTypes,
    int? LookbackDays, bool ParticipantRequired, string RequiredStatus, int MinimumQualityVersion, bool PartialAllowed, bool CompletenessRequired);
public sealed record FeatureCoverageDecision(string FeatureName, FeatureCoverageOutcome Outcome, IReadOnlyList<Guid> EvidenceIds, IReadOnlyList<Guid> ReviewIds, IReadOnlyList<string> Reasons);
public sealed record EventTimeSubmission(Guid DateObservationId, Guid RawId, EventTimeValue Value, string EvidenceReference,
    Guid? CorrectsId, DateTime? PublicationUtc, DateTime AvailableUtc, string OperatorId, string Reason);
public sealed record EventTimeResolution(DateTime? UtcInstant, EventTimePrecision Precision, string Reason);
public sealed record EventTimeClaimResult(EventTimeEvidence Evidence, Guid IdentityDecisionId, Guid EventId, EventTimeResolution Resolution);
public sealed record DatasetGovernanceRow(Guid EventId, DateTime PredictionCutoffUtc, int CoverageSchemaVersion,
    IReadOnlyList<CoverageReport> Coverage, IReadOnlyList<FeatureCoverageDecision> Gates, IReadOnlyList<EventTimeClaimResult> EventTimes,
    EventTimeResolution DateObservationPrecision, IReadOnlyList<DatasetFrozenRecord> FrozenRecords);
public sealed record DatasetGovernance(int SchemaVersion, IReadOnlyList<DatasetGovernanceRow> Rows);
public interface IHistoricalCoverage
{
    Task<CoverageEvidence> RecordAsync(CoverageSubmission request, CancellationToken token = default);
    Task<CoverageReview> ReviewAsync(CoverageReviewRequest request, CancellationToken token = default);
    Task<CoverageEvidence> InspectAsync(Guid id, CancellationToken token = default);
    Task<CoverageReport> ReportAsync(CoverageQuery query, CancellationToken token = default);
    Task<EventTimeEvidence> RecordTimeAsync(EventTimeSubmission request, CancellationToken token = default);
    Task<IReadOnlyList<EventTimeClaimResult>> TimesAsync(Guid identityId, DateTime asOf, DatasetMode mode, DateTime? reconstruction, DataPurpose purpose, UsageContext context, CancellationToken token = default);
    Task<DatasetGovernanceRow> DatasetRowAsync(DatasetDefinition definition, DatasetRow row, CancellationToken token = default);
    Task<bool> VerifyFrozenAsync(DatasetGovernance governance, CancellationToken token = default);
    Task EnsureCurrentAsync(DatasetGovernance governance, DatasetDefinition definition, CancellationToken token = default);
}
