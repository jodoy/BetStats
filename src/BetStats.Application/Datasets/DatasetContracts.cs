using BetStats.Application.Governance;
using BetStats.Domain.Governance;
using BetStats.Domain.Quality;
using BetStats.Application.Coverage;
using System.Text.Json.Serialization;

namespace BetStats.Application.Datasets;

public enum DatasetBuildStatus { Requested, Running, Succeeded, Failed, Cancelled }
public sealed record DatasetTarget(Guid DateObservationId, DateTime PredictionCutoffUtc);
public sealed record DatasetDefinition(int Version, Guid SportId, Guid CompetitionId, Guid SeasonId,
    string CompetitionReference, string SeasonReference, DateOnly SeasonStart, DateOnly SeasonEnd,
    DateTime AsOfUtc, DatasetMode Mode, DateTime? ReconstructionAtUtc, int QualityVersion,
    int FeatureSchemaVersion, DataPurpose Purpose, UsageContext Context, string CalendarBasis,
    string InclusionRule, string ExclusionRule, IReadOnlyList<DatasetTarget> Targets)
{
    public void Validate()
    {
        static bool Utc(DateTime t) => t.Kind == DateTimeKind.Utc && t.Ticks % 10 == 0;
        if (Version is not (1 or 2) || SportId == Guid.Empty || CompetitionId == Guid.Empty || SeasonId == Guid.Empty ||
            string.IsNullOrWhiteSpace(CompetitionReference) || CompetitionReference.Length > 50 ||
            string.IsNullOrWhiteSpace(SeasonReference) || SeasonReference.Length > 50 || SeasonEnd < SeasonStart ||
            SeasonEnd.DayNumber - SeasonStart.DayNumber > 730 || !Utc(AsOfUtc) || !Enum.IsDefined(Mode) ||
            QualityVersion != 1 || FeatureSchemaVersion != Version || CalendarBasis != "UTC-calendar" ||
            InclusionRule != "eligible-observed-metadata-v1" || ExclusionRule != "fail-closed-v1" ||
            Context is null || Context.IntendedRetentionDays is <= 0 ||
            Purpose is not (DataPurpose.InternalAnalytics or DataPurpose.ModelTraining or DataPurpose.PublicDisplay or DataPurpose.CommercialUse or DataPurpose.Redistribution) ||
            Targets is null || Targets.Count is < 1 or > 100 || Targets.Any(t => t is null) || Targets.Select(t => t.DateObservationId).Distinct().Count() != Targets.Count ||
            Targets.Any(t => t.DateObservationId == Guid.Empty || !Utc(t.PredictionCutoffUtc) || t.PredictionCutoffUtc > AsOfUtc) ||
            Mode == DatasetMode.HistoricalAsKnown && ReconstructionAtUtc is not null ||
            Mode == DatasetMode.RetrospectiveReconstruction && (ReconstructionAtUtc is not { } r || !Utc(r) || r < AsOfUtc))
            throw new ArgumentException("Explicit supported dataset scope, versions, rules, UTC cutoffs and bounded targets required.");
    }
}
public sealed record DatasetBuildRequest(DatasetDefinition Definition, string OperatorId, string Reason);
public sealed record DatasetBuildResult(Guid AttemptId, DatasetBuildStatus Status, Guid? SnapshotId, string? ManifestHash, string? FailureCode);
public sealed record DatasetPolicyReference(Guid PolicyId, DataPurpose Purpose, int Version, IReadOnlyList<Guid> AuditIds);
public sealed record DatasetFrozenRecord(string Kind, Guid Id, string CanonicalJson);
public sealed record DatasetEvidenceReference(Guid SourceId, Guid EventId, Guid HomeId, Guid AwayId, Guid ProviderIdentityId,
    IReadOnlyList<Guid> ContextIdentityIds, IReadOnlyList<Guid> DecisionIds, Guid DateObservationId, Guid? StatusObservationId,
    Guid RawId, string RawHash, DateTime RawRecordedUtc, DateTime DateAvailableUtc, DateTime DateRecordedUtc,
    DateOnly EventDate, string? Status, IReadOnlyList<Guid> QualityAssessmentIds, IReadOnlyList<DatasetPolicyReference> Policies,
    DateTime EvidenceCutoffUtc, DateTime InterpretationCutoffUtc, IReadOnlyList<DatasetFrozenRecord> FrozenRecords);
public sealed record FeatureArtifact(int SchemaVersion, DatasetEvidenceReference Target, IReadOnlyList<DatasetEvidenceReference> History, FeatureVector Vector,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DatasetGovernanceRow? Governance = null);
public sealed record DatasetEligibilityFailure(Guid ObservationId, string Reason);
public sealed record DatasetRow(Guid EventId, DateTime PredictionCutoffUtc, DatasetEvidenceReference Target,
    IReadOnlyList<DatasetEvidenceReference> History, IReadOnlyList<DatasetEligibilityFailure> Excluded,
    FeatureVector Features, string FeatureHash);
public sealed record DatasetManifest(int ManifestVersion, int SerializerVersion, string DefinitionFingerprint,
    DatasetDefinition Definition, IReadOnlyList<string> QualityRuleVersions, IReadOnlyList<DatasetRow> Rows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DatasetGovernance? Governance = null);
public sealed record DatasetSnapshot(Guid Id, string ManifestHash, DateTime BuiltAtUtc, DateTime RecordedAtUtc, DatasetManifest Manifest);
public sealed record DatasetVerification(Guid SnapshotId, bool ArtifactIntegrity, bool EvidenceComplete,
    bool CurrentlyAuthorized, bool FeaturesReproducible, IReadOnlyList<string> Reasons);
public sealed record DatasetDifference(string Path, string? Left, string? Right);
public sealed record DatasetComparison(Guid LeftId, Guid RightId, int Offset, int Total, IReadOnlyList<DatasetDifference> Differences);
public interface IDatasets
{
    Task<DatasetBuildResult> BuildAsync(DatasetBuildRequest request, CancellationToken token = default);
    Task<DatasetSnapshot> InspectAsync(Guid id, CancellationToken token = default);
    Task<DatasetVerification> VerifyAsync(Guid id, CancellationToken token = default);
    Task<DatasetComparison> CompareAsync(Guid left, Guid right, int offset, int limit, CancellationToken token = default);
    Task MarkInterruptedAsync(Guid attemptId, string actor, string reason, CancellationToken token = default);
}
