using BetStats.Application.Evaluation;
using BetStats.Application.Ingestion;

namespace BetStats.Application.Pipeline;

public enum ValidationEvidenceKind { Unknown, Synthetic, OperatorDeclaredReal }
public enum ValidationReadiness { Verified, Incomplete, Blocked, Unavailable }
public sealed record ValidationPublication(Guid PredictionExecutionId, Guid? EvaluationExecutionId = null);
public sealed record FootballValidationInput(int Version, Guid SourceId, Guid RawId, string ExpectedRawHash,
    string Profile, FootballImportScope Scope, ValidationEvidenceKind EvidenceKind, IReadOnlyList<ValidationPublication> Publications)
{
    public void Validate()
    {
        if (Version != 1 || SourceId == Guid.Empty || RawId == Guid.Empty || !BacktestRules.Hash(ExpectedRawHash) ||
            Profile is not ("football-history-v1" or "results-v1" or "metadata-v1" or "api-football-fixtures-v1") ||
            Scope is null || !Scope.IsValid || !Enum.IsDefined(EvidenceKind) || Publications is null || Publications.Count > 3 ||
            Publications.Any(p => p.PredictionExecutionId == Guid.Empty || p.EvaluationExecutionId == Guid.Empty) ||
            Publications.Select(p => p.PredictionExecutionId).Distinct().Count() != Publications.Count)
            throw new ArgumentException("Explicit supported bounded football validation input required.");
    }
}
public sealed record ValidationIssue(string Code, int Count);
public sealed record ValidationObservation(Guid ObservationId, Guid EventId, DateOnly EventDate, DateTime? SourcePublishedUtc,
    DateTime RetrievedUtc, DateTime AvailableUtc, DateTime RecordedUtc);
public sealed record ValidationAuthorization(BetStats.Domain.Governance.DataPurpose Purpose, Guid PolicyId, int PolicyVersion,
    int? MaximumRetentionDays, bool AttributionRequired);
public sealed record ValidationModel(Guid PredictionExecutionId, Guid? EvaluationExecutionId, Guid BacktestId,
    string BacktestHash, string Predictor, int Version, Guid DatasetId, string DatasetHash,
    DateTime PlannedCutoffUtc, DateTime ActualCutoffUtc, DateTime ExecutedUtc, DateTime PublishedUtc,
    DateTime? EvaluationCutoffUtc, int Predictions, int WarmupExclusions, IReadOnlyList<TargetEvaluation> Targets);
public sealed record FootballValidationReport(int Version, string InputHash, Guid SourceId, Guid RawId, string RawHash,
    string Profile, FootballImportScope Scope, ValidationEvidenceKind EvidenceKind, ValidationReadiness Readiness,
    DateTime AssessedUtc, DateTime RetrievedUtc, DateTime RecordedUtc, int ParsedRecords, int ImportedEvents,
    int EligibleIdentities, int MissingResults, int UnknownKickoffs, IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<ValidationModel> Models, IReadOnlyList<string> Limitations, IReadOnlyList<ValidationObservation> ResultObservations,
    IReadOnlyList<ValidationAuthorization> Authorizations);
public sealed record FootballValidationArtifact(Guid ExecutionId, string Hash, FootballValidationReport Report);
public interface IFootballValidationReports
{
    Task<FootballValidationArtifact> ReadAsync(Guid executionId, bool display, CancellationToken token = default);
}
public static class FootballValidationRules
{
    public static ValidationReadiness Readiness(int parsed, int imported, int identities, int issues, int models, int eligible) =>
        issues > 0 ? ValidationReadiness.Blocked : parsed == 0 ? ValidationReadiness.Unavailable :
        imported != parsed || identities != parsed || models == 0 || eligible == 0 ? ValidationReadiness.Incomplete : ValidationReadiness.Verified;
}
