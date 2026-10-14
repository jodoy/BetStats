using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Domain.Coverage;

namespace BetStats.Application.Providers;

public enum AcquisitionCategory { HistoricalResults, PrematchMetadata, PostmatchResults, KickoffEvidence }
public enum AcquisitionState { Verified, Partial, Blocked, Unavailable }
public sealed record AcquisitionCapability(int Version, Guid SourceId, string Provider, Guid PolicyId, int PolicyVersion,
    FootballTransport Transport, string Profile, IReadOnlyList<AcquisitionCategory> Categories, int MaximumBytes = 1048576, int MaximumPages = 1)
{
    public void Validate()
    {
        if (Version != 1 || SourceId == Guid.Empty || PolicyId == Guid.Empty || PolicyVersion < 1 ||
            string.IsNullOrWhiteSpace(Provider) || Provider.Length > 100 || Provider.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            !Enum.IsDefined(Transport) || Profile is not ("football-history-v1" or "source-event-time-v1" or "api-football-fixtures-v1") || Categories is null || Categories.Count is < 1 or > 4 ||
            Categories.Distinct().Count() != Categories.Count || Categories.Any(c => !Enum.IsDefined(c)) ||
            MaximumBytes is < 1 or > 1048576 || MaximumPages is < 1 or > 100 || Transport == FootballTransport.LocalCsv && MaximumPages != 1)
            throw new ArgumentException("Supported bounded acquisition capability required; technical capabilities grant no rights.");
    }
}
public sealed record AcquisitionPlan(int Version, AcquisitionCapability Capability, FootballImportScope Scope, AcquisitionCategory Category,
    string Hash, long Length, ValidationEvidenceKind EvidenceKind, Guid? DateObservationId = null, Guid? CorrectsKickoffId = null)
{
    public void Validate()
    {
        if (Capability is null) throw new ArgumentException("Capability required.");
        Capability.Validate();
        if (Version != 1 || Scope is null || !Scope.IsValid || !Enum.IsDefined(Category) || !Capability.Categories.Contains(Category) ||
            !BacktestRules.Hash(Hash) || Length < 1 || Length > Capability.MaximumBytes || !Enum.IsDefined(EvidenceKind) ||
            Category == AcquisitionCategory.KickoffEvidence != (Capability.Profile == "source-event-time-v1") ||
            Category == AcquisitionCategory.KickoffEvidence && (DateObservationId is null || DateObservationId == Guid.Empty) ||
            Category != AcquisitionCategory.KickoffEvidence && (DateObservationId is not null || CorrectsKickoffId is not null) || CorrectsKickoffId == Guid.Empty)
            throw new ArgumentException("Explicit acquisition scope, profile, hash and observation binding required.");
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Fingerprint => CanonicalDatasetJson.Fingerprint(this);
}
public sealed record AcquisitionRaw(Guid Id, string Hash, long Length, DateTime RetrievedUtc, DateTime RecordedUtc);
public sealed record AcquisitionObservation(Guid EventId, Guid DateObservationId, Guid IdentityDecisionId, Guid HomeId, Guid AwayId,
    Guid CompetitionId, Guid SeasonId, DateOnly EventDate, DateTime AvailableUtc, DateTime RecordedUtc,
    DateTime? SourcePublishedUtc, DateTime? KickoffUtc, EventTimePrecision Precision, Guid? KickoffEvidenceId,
    DateTime? KickoffRecordedUtc, DateTime? KickoffAvailableUtc, Guid? ResultObservationId, bool FinalResultEligible, bool HistoricalImport, IReadOnlyList<string> Exclusions,
    DateTime? SourceEventUtc = null, DateTime? ResultPublishedUtc = null, DateTime? ResultAvailableUtc = null, DateTime? ResultRecordedUtc = null, EventTimeValue? SourceKickoff = null);
public sealed record AcquisitionReport(int Version, string PlanHash, AcquisitionCapability Capability, FootballImportScope Scope,
    AcquisitionCategory Category, ValidationEvidenceKind EvidenceKind, AcquisitionState State, DateTime AssessedUtc,
    Guid OperationId, string ReconciliationOutcome, bool DuplicateContent, AcquisitionRaw Raw,
    IReadOnlyList<AcquisitionObservation> Observations, IReadOnlyList<string> Limitations, IReadOnlyList<AcquisitionRaw> EvidenceRaws);
public sealed record AcquisitionArtifact(Guid ExecutionId, string Hash, AcquisitionReport Report);
public sealed record ProspectiveCohortInput(int Version, Guid SourceId, IReadOnlyList<Guid> AcquisitionExecutions,
    IReadOnlyList<Guid> ValidationExecutions, IReadOnlyList<CoverageQuery> CoverageQueries)
{
    public void Validate()
    {
        if (Version != 1 || SourceId == Guid.Empty || AcquisitionExecutions is null || AcquisitionExecutions.Count is < 1 or > 10 ||
            AcquisitionExecutions.Any(id => id == Guid.Empty) || AcquisitionExecutions.Distinct().Count() != AcquisitionExecutions.Count ||
            ValidationExecutions is null || ValidationExecutions.Count > 3 || ValidationExecutions.Any(id => id == Guid.Empty) ||
            ValidationExecutions.Distinct().Count() != ValidationExecutions.Count || CoverageQueries is null || CoverageQueries.Count > 2 ||
            CoverageQueries.Any(q => q is null || q.Scope is null || q.Scope.SourceId != SourceId)) throw new ArgumentException("Bounded source-bound cohort required.");
    }
}
public sealed record ProspectiveCoverage(string Status, string Category, IReadOnlyList<string> Reasons,
    CoverageScope? Scope = null, IReadOnlyList<Guid>? EvidenceIds = null, IReadOnlyList<Guid>? ReviewIds = null,
    IReadOnlyList<AcquisitionRaw>? EvidenceRaws = null, DateTime? ValidUntilUtc = null);
public sealed record ProspectiveCohortReport(int Version, string InputHash, Guid SourceId, DateTime AssessedUtc,
    AcquisitionState State, int ObservedEvents, int PreEventObserved, int FrozenPredictions, int EligiblePredictions,
    int CompletedMatches, int EligibleEvaluations, int WarmupExclusions, IReadOnlyList<AcquisitionArtifact> Acquisitions,
    IReadOnlyList<FootballValidationArtifact> Validations, IReadOnlyList<ProspectiveCoverage> Coverage,
    IReadOnlyList<string> Limitations);
public sealed record ProspectiveCohortArtifact(Guid ExecutionId, string Hash, ProspectiveCohortReport Report);
public sealed record AcquisitionExecutionStatus(Guid ExecutionId, PipelineState State, int Attempt, DateTime PlannedUtc, DateTime StartedUtc, DateTime? CompletedUtc);
public sealed record AcquisitionJobStatus(Guid JobId, PipelineState State, bool Enabled, bool CurrentlyAuthorized, IReadOnlyList<AcquisitionExecutionStatus> Executions);
public interface IFootballAcquisitionReports
{
    Task<AcquisitionArtifact> AcquisitionAsync(Guid execution, bool display, CancellationToken token = default);
    Task<ProspectiveCohortArtifact> CohortAsync(Guid execution, bool display, CancellationToken token = default);
    Task<AcquisitionJobStatus> StatusAsync(Guid jobId, bool display, CancellationToken token = default);
}
public static class ProspectiveEvidenceRules
{
    public static bool KnownBefore(AcquisitionObservation receipt, DateTime cutoff) => BacktestRules.Utc(cutoff) &&
        receipt.EventId != Guid.Empty && receipt.IdentityDecisionId != Guid.Empty && receipt.HomeId != Guid.Empty && receipt.AwayId != Guid.Empty &&
        receipt.HomeId != receipt.AwayId && receipt.Precision is EventTimePrecision.Minute or EventTimePrecision.Second &&
        receipt.KickoffUtc is { } kickoff && kickoff > cutoff && receipt.RecordedUtc < cutoff && receipt.AvailableUtc <= cutoff &&
        receipt.KickoffRecordedUtc is { } recorded && recorded < cutoff && receipt.KickoffAvailableUtc <= cutoff && receipt.Exclusions.Count == 0;
}
