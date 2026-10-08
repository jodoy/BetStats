namespace BetStats.Domain.Quality;

public enum QualitySeverity { Info, Warning, Error, Critical }
public enum QualityClassification { Accepted, Invalid, DuplicateEquivalent, DuplicateContradictory, IdentityAmbiguous, CanonicalMismatch, ObservationConflict, HistoricalCorrection, InvalidTransition, Superseded }
public sealed record QualityRule(string Id, int Version);
public sealed record QualityIssue(QualityRule Rule, bool Passed, QualitySeverity Severity, bool BlocksEligibility, string ReasonCode,
    QualityClassification Classification = QualityClassification.Accepted);
public enum DatasetMode { HistoricalAsKnown, RetrospectiveReconstruction }
public sealed record QualityGateResult(bool Eligible, DatasetMode Mode, DateTime AsOfUtc, DateTime? ReconstructionAtUtc,
    Guid ObservationId, Guid? FrozenTargetId, Guid? InterpretedTargetId, Guid? DecisionId, Guid? PolicyId,
    IReadOnlyList<string> Reasons, IReadOnlyList<Guid> AssessmentIds, IReadOnlyList<Guid>? ConflictObservationIds = null);

// No provider-specific column names or parsing in Domain.
public sealed class QualityAssessment
{
    public Guid Id { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid DataSourceId { get; init; }
    public Guid RawPayloadId { get; init; }
    public Guid? RunId { get; init; }
    public Guid? ProviderIdentityId { get; init; }
    public Guid? ObservationId { get; init; }
    public Guid? PolicyId { get; init; }
    public int Row { get; init; }
    public string RecordReference { get; init; } = "";
    public string? ContextKey { get; init; }
    public Guid SportId { get; init; }
    public string RuleId { get; init; } = "";
    public int RuleVersion { get; init; }
    public bool Passed { get; init; }
    public QualitySeverity Severity { get; init; }
    public bool BlocksEligibility { get; init; }
    public string ReasonCode { get; init; } = "";
    public QualityClassification Classification { get; init; }
    public DateTime AssessedAtUtc { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}

public sealed class MaintenanceEvent
{
    public Guid Id { get; init; }
    public Guid ExecutionId { get; init; }
    public int Sequence { get; init; }
    public string Action { get; init; } = "";
    public string OperatorId { get; init; } = "";
    public string Reason { get; init; } = "";
    public Guid TargetId { get; init; }
    public Guid? PreviousReferenceId { get; init; }
    public Guid? DecisionId { get; init; }
    public string Result { get; init; } = "";
    public DateTime ExecutedAtUtc { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}
