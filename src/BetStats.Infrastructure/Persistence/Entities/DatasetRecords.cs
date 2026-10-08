using BetStats.Application.Datasets;

namespace BetStats.Infrastructure.Persistence.Entities;

public sealed class DatasetArtifact
{
    public Guid Id { get; init; }
    public required string DefinitionFingerprint { get; init; }
    public required string ManifestHash { get; init; }
    public required byte[] Content { get; init; }
    public int RowCount { get; init; }
    public int FeatureSchemaVersion { get; init; }
    public DateTime BuiltAtUtc { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}
public sealed class DatasetFeatureRecord
{
    public Guid Id { get; init; }
    public Guid DatasetId { get; init; }
    public Guid EventId { get; init; }
    public DateTime PredictionCutoffUtc { get; init; }
    public required string Fingerprint { get; init; }
    public required byte[] Content { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}
public sealed class DatasetBuildEvent
{
    public Guid Id { get; init; }
    public Guid AttemptId { get; init; }
    public int Sequence { get; init; }
    public DatasetBuildStatus Status { get; init; }
    public required string DefinitionFingerprint { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    public string? FailureCode { get; init; }
    public Guid? SnapshotId { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}
