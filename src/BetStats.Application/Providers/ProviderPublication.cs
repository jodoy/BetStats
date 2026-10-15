using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;

namespace BetStats.Application.Providers;

public enum ProviderPublicationState { Published, ReviewRequired, Blocked }
public sealed record ProviderPublicationInput(int Version, Guid RawId, Guid SourceId, string SourceCode,
    string RawHash, Guid PolicyId, int PolicyVersion, FootballImportScope Scope, ApiFootballCanary Canary,
    ValidationEvidenceKind EvidenceKind, string Profile = "api-football-fixtures-v1");
public sealed record PublicationBinding(Guid IdentityId, Guid? DecisionId, int Version, Guid? CanonicalId, string Reference);
public sealed record PublishedFixture(string Reference, Guid? EventId, IReadOnlyList<PublicationBinding> Bindings,
    Guid? DateObservationId, Guid? ScheduleObservationId, Guid? StatusObservationId, Guid? TimeEvidenceId,
    Guid? ResultObservationId, string ProviderStatus, IReadOnlyList<string> Exclusions,
    DateTime? KickoffUtc = null, DateTime? SourcePublicationUtc = null,
    Guid? ScheduleCorrectsId = null, Guid? ResultCorrectsId = null, Guid? StatusCorrectsId = null);
public sealed record ProviderPublicationReport(int Version, Guid ReceiptId, string Key, ProviderPublicationInput Input,
    int MappingVersion, Guid JobId, Guid ExecutionId, int Attempt, Guid Owner, DateTime RetrievedUtc,
    DateTime RawRecordedUtc, DateTime PublishedUtc, ProviderPublicationState State, IReadOnlyList<PublishedFixture> Fixtures,
    string Provider = "api-football");
public sealed record ProviderPublicationArtifact(ProviderPublicationReport Report, string Hash, DateTime RecordedUtc, bool Verified);
public sealed record ProviderPublicationStatus(Guid JobId, PipelineState State, IReadOnlyList<ProviderPublicationArtifact> Receipts,
    IReadOnlyList<AcquisitionExecutionStatus> Attempts, Guid RawId, string RawHash, string Profile, FootballImportScope Scope,
    string RawState, string ParsingState, IReadOnlyList<PublicationOperationState> Operations);
public sealed record PublicationOperationState(Guid? ExecutionId, PipelineState State, string Category, DateTime RecordedUtc);
public interface IProviderPublicationReports
{
    Task<ProviderPublicationArtifact> VerifyAsync(Guid receiptId, InspectionAudience display, CancellationToken token = default);
    Task<ProviderPublicationStatus> StatusAsync(Guid jobId, InspectionAudience display, CancellationToken token = default);
}
